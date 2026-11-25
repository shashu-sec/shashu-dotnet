using System.Net.Http.Json;
using System.Text.Json;
using Shashu.Configuration;

namespace Shashu.Tracing;

/// <summary>
/// Shashu's own hierarchical-span tracer. Symmetric with the Python
/// SDK's <c>tracer.span()</c> + <c>@tracer.trace()</c> contract — NOT
/// W3C OpenTelemetry. Spans are buffered in-process and flushed to the
/// gateway's <c>POST /api/telemetry/spans</c> endpoint either on demand
/// (<see cref="Flush"/>) or on application exit (<see cref="ShutdownHook"/>).
/// </summary>
/// <remarks>
/// <para>What gets emitted on the wire:</para>
/// <list type="bullet">
///   <item><c>x-shashu-trace-id</c> — the active span's trace id (or
///         the workflow-context default when no span is active).</item>
///   <item><c>x-shashu-span-id</c> — the active span's span id.</item>
///   <item><c>x-shashu-parent-id</c> — the active span's parent
///         span id. Empty for root spans.</item>
/// </list>
///
/// <para>Use the tracer to instrument workflow steps:</para>
/// <code>
/// using (Tracer.Span("retrieval"))
/// {
///     // gateway calls in here are tagged with this span's trace+span+parent
///     await client.Chat.Completions.CreateAsync(retrievalRequest);
///
///     using (Tracer.Span("answer"))  // nested span
///     {
///         await client.Chat.Completions.CreateAsync(answerRequest);
///     }
/// }
/// // span auto-flushes on dispose
/// </code>
///
/// <para>Threading: built on <see cref="AsyncLocal{T}"/> so it works
/// transparently across <c>await</c> boundaries. Each logical async
/// flow has its own span stack.</para>
/// </remarks>
public static class Tracer
{
    // ----- per-async-flow state ----- (AsyncLocal so awaits propagate it)

    private static readonly AsyncLocal<string?> _traceId = new();
    private static readonly AsyncLocal<string?> _currentSpanId = new();
    private static readonly AsyncLocal<string?> _parentSpanId = new();

    // ----- captured inbound A2A flow ----- (WP-A2A-4 / ADR-007 §0, §4.1)
    // When this agent is SERVING a request, CaptureIncoming() stores the inbound flow
    // here so a downstream gateway call PROPAGATES it (trace + session + hop + caller
    // lineage) instead of minting a fresh one. All null/0 => origin (behavior unchanged).
    private static readonly AsyncLocal<string?> _capturedTraceId = new();
    private static readonly AsyncLocal<string?> _sessionId = new();
    private static readonly AsyncLocal<int> _inboundHopDepth = new();
    private static readonly AsyncLocal<string?> _inboundCallerAgentId = new();

    // ----- buffered span telemetry, drained by Flush() -----

    private static readonly object _bufferLock = new();
    private static readonly List<Dictionary<string, object?>> _buffer = new();

    // Captured at first use so the tracer doesn't depend on any specific
    // ShashuClient instance — symmetric with the Python SDK.
    private static CredentialResolver? _resolver;
    private static string? _gatewayBaseUrl;

    // Latched so an unconfigured tracer warns once rather than on every flush.
    // 0 = not yet warned; Interlocked because Flush() can be called from a
    // shutdown hook while a request thread is still flushing.
    private static int _warnedUnconfigured;
    private static HttpClient? _flushHttp;
    private static int _shutdownRegistered;

    /// <summary>Whether the calling async flow has an active span.</summary>
    public static bool IsActive => _currentSpanId.Value is not null;

    /// <summary>
    /// Active trace id. Prefers the active span's trace; otherwise the captured
    /// mid-flow trace (so a downstream gateway call propagates the flow even with no
    /// span); null at origin with no span.
    /// </summary>
    public static string? TraceId => _traceId.Value ?? _capturedTraceId.Value;

    /// <summary>Active span id, or null when no span is active.</summary>
    public static string? CurrentSpanId => _currentSpanId.Value;

    /// <summary>Parent span id of the active span, or null for root spans.</summary>
    public static string? ParentSpanId => _parentSpanId.Value;

    // ------------------------------------------------------------------ //
    // A2A inbound-capture + outbound propagation (ADR-007 §0, §4.1 locked)
    // ------------------------------------------------------------------ //

    /// <summary>
    /// True when this async flow CAPTURED an inbound flow — so a downstream gateway
    /// call should PROPAGATE the trace and attach caller/session/hop lineage rather
    /// than originate a new flow. False = origin (header set unchanged).
    /// </summary>
    public static bool IsA2aHop => _capturedTraceId.Value is not null;

    /// <summary>The captured inbound session id, or null when none was propagated.</summary>
    public static string? SessionId => string.IsNullOrEmpty(_sessionId.Value) ? null : _sessionId.Value;

    /// <summary>Advisory inbound hop depth captured from x-shashu-hop-depth (0 if absent).</summary>
    public static int InboundHopDepth => _inboundHopDepth.Value;

    /// <summary>
    /// Hop depth to advertise on the next downstream call = inbound + 1 (advisory; the
    /// gateway recounts authoritatively from its a2a:hop:{trace_id} counter).
    /// </summary>
    public static int OutboundHopDepth => _inboundHopDepth.Value + 1;

    /// <summary>
    /// The captured inbound x-shashu-caller-agent-id — the upstream agent that INVOKED
    /// this hop. The SDK FORWARDS this on the outbound gateway call so the gateway records
    /// the real (invoker → this agent) edge, NEVER this agent's own id (§4.1). null at
    /// origin / caller-stripped.
    /// </summary>
    public static string? InboundCallerAgentId =>
        string.IsNullOrEmpty(_inboundCallerAgentId.Value) ? null : _inboundCallerAgentId.Value;

    /// <summary>
    /// Capture an inbound flow from the headers of a request this agent is SERVING, so a
    /// downstream call made through the SDK propagates the SAME flow (WP-A2A-4 / ADR-007 §0).
    /// Reads (case-insensitively) <c>x-shashu-trace-id</c> (the flow id),
    /// <c>x-shashu-session-id</c>, <c>x-shashu-hop-depth</c>, and
    /// <c>x-shashu-caller-agent-id</c> (the upstream INVOKER). The captured trace is
    /// PROPAGATED on the next outbound call (never re-minted); the session, inbound hop
    /// depth, and inbound caller ride along — the caller is FORWARDED on the gateway call
    /// (§4.1), never replaced by self.
    /// <para>
    /// One-shot: the capture persists for the remainder of the current async flow. Servers
    /// handling many requests should prefer <c>Incoming</c> (a scoped <c>using</c>) so one
    /// flow's lineage never leaks into the next. No inbound trace ⇒ origin: the ambient
    /// capture is cleared and <c>false</c> is returned. Never throws.
    /// </para>
    /// </summary>
    /// <returns>True if a trace was captured (mid-flow hop); false at origin.</returns>
    public static bool CaptureIncoming(IReadOnlyDictionary<string, string> headers) =>
        ApplyCapture(Coerce(headers));

    /// <summary>
    /// Overload accepting <see cref="System.Net.Http.Headers.HttpHeaders"/> (e.g. an
    /// incoming <c>HttpRequestMessage.Headers</c>). See <c>CaptureIncoming</c>.
    /// </summary>
    public static bool CaptureIncoming(System.Net.Http.Headers.HttpHeaders headers) =>
        ApplyCapture(Coerce(headers));

    /// <summary>
    /// Scoped inbound capture: capture for the duration of serving ONE request, then
    /// restore the prior context when the returned scope is disposed — so a long-lived
    /// worker does not leak one flow's lineage into the next. Prefer this in servers;
    /// <c>CaptureIncoming</c> is the one-shot.
    /// <code>
    /// using (Tracer.Incoming(request.Headers))
    /// {
    ///     await client.Chat.Completions.CreateAsync(...); // propagates the captured flow
    /// }
    /// </code>
    /// </summary>
    public static IDisposable Incoming(IReadOnlyDictionary<string, string> headers) =>
        new A2aScope(Coerce(headers));

    /// <summary>Overload accepting <see cref="System.Net.Http.Headers.HttpHeaders"/>.</summary>
    public static IDisposable Incoming(System.Net.Http.Headers.HttpHeaders headers) =>
        new A2aScope(Coerce(headers));

    /// <summary>
    /// Set the captured A2A state from a normalized (lowercase-keyed) header map. Clears
    /// to origin when no inbound trace is present. Returns true when a trace was captured.
    /// </summary>
    private static bool ApplyCapture(Dictionary<string, string> h)
    {
        var trace = h.TryGetValue("x-shashu-trace-id", out var t) ? t.Trim() : "";
        if (string.IsNullOrEmpty(trace))
        {
            // Origin: clear any ambient capture so non-A2A behavior is unchanged.
            _capturedTraceId.Value = null;
            _sessionId.Value = null;
            _inboundHopDepth.Value = 0;
            _inboundCallerAgentId.Value = null;
            return false;
        }
        _capturedTraceId.Value = trace;
        _sessionId.Value = h.TryGetValue("x-shashu-session-id", out var s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;
        _inboundHopDepth.Value = ParseHopDepth(h.TryGetValue("x-shashu-hop-depth", out var hd) ? hd : null);
        _inboundCallerAgentId.Value = h.TryGetValue("x-shashu-caller-agent-id", out var c) && !string.IsNullOrWhiteSpace(c) ? c.Trim() : null;
        return true;
    }

    /// <summary>Inbound hop depth as a non-negative int; 0 on absent/garbage (advisory).</summary>
    private static int ParseHopDepth(string? raw) =>
        int.TryParse((raw ?? "").Trim(), out var n) && n > 0 ? n : 0;

    /// <summary>Lowercase-keyed map from a string dictionary (tolerant; never throws).</summary>
    private static Dictionary<string, string> Coerce(IReadOnlyDictionary<string, string>? headers)
    {
        var o = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (headers is null) return o;
        foreach (var kv in headers)
            if (kv.Key is not null && kv.Value is not null) o[kv.Key] = kv.Value;
        return o;
    }

    /// <summary>Lowercase-keyed map from HttpHeaders (first value per key).</summary>
    private static Dictionary<string, string> Coerce(System.Net.Http.Headers.HttpHeaders? headers)
    {
        var o = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (headers is null) return o;
        foreach (var kv in headers)
        {
            var v = kv.Value?.FirstOrDefault();
            if (kv.Key is not null && v is not null) o[kv.Key] = v;
        }
        return o;
    }

    /// <summary>Scoped capture: snapshots prior A2A state and restores it on dispose.</summary>
    private sealed class A2aScope : IDisposable
    {
        private readonly string? _priorTrace;
        private readonly string? _priorSession;
        private readonly int _priorHop;
        private readonly string? _priorCaller;
        private bool _disposed;

        public A2aScope(Dictionary<string, string> headers)
        {
            _priorTrace = _capturedTraceId.Value;
            _priorSession = _sessionId.Value;
            _priorHop = _inboundHopDepth.Value;
            _priorCaller = _inboundCallerAgentId.Value;
            ApplyCapture(headers);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _capturedTraceId.Value = _priorTrace;
            _sessionId.Value = _priorSession;
            _inboundHopDepth.Value = _priorHop;
            _inboundCallerAgentId.Value = _priorCaller;
        }
    }

    /// <summary>
    /// Configure the tracer's flush target. Call once at app startup —
    /// idempotent if called multiple times. If unset, the tracer reads
    /// <c>SHASHU_API_KEY</c> + <c>SHASHU_BASE_URL</c> at flush time.
    /// </summary>
    public static void Configure(
        string? apiKey,
        string? gatewayBaseUrl = null,
        Func<CancellationToken, ValueTask<string>>? tokenProvider = null)
    {
        if (apiKey is not null || tokenProvider is not null)
            _resolver = new CredentialResolver(apiKey: apiKey, tokenProvider: tokenProvider, requireOne: false);
        if (!string.IsNullOrEmpty(gatewayBaseUrl))
            _gatewayBaseUrl = gatewayBaseUrl.TrimEnd('/');

        // Lazy-register shutdown hook so spans flush on process exit even
        // if the customer never calls Flush() explicitly. Idempotent.
        if (Interlocked.CompareExchange(ref _shutdownRegistered, 1, 0) == 0)
        {
            AppDomain.CurrentDomain.ProcessExit += (_, _) => ShutdownHook();
        }
    }

    /// <summary>
    /// Open a new span. The returned <see cref="IDisposable"/> closes
    /// the span (records duration + buffers for flush) on disposal.
    /// </summary>
    /// <param name="name">Human-readable span name (e.g. <c>"retrieval"</c>).</param>
    /// <param name="attributes">Free-form metadata recorded with the span.</param>
    public static IDisposable Span(string name, IReadOnlyDictionary<string, object?>? attributes = null)
    {
        // First span in the async flow → start a new trace. Nested spans inherit the
        // trace; a span opened inside a captured A2A flow inherits the captured trace so
        // the flow stays one lineage (parity with the Python/Node reference).
        var traceId = _traceId.Value ?? _capturedTraceId.Value ?? NewId();
        // Snapshot the state this span displaces so Dispose can restore it
        // verbatim (same save/restore pattern as A2aScope) — the prior current
        // span is also this span's parent.
        var priorSpanId = _currentSpanId.Value;
        var priorParentId = _parentSpanId.Value;
        var parentId = priorSpanId;  // null for root span
        var spanId = NewId();
        var startUtc = DateTimeOffset.UtcNow;

        _traceId.Value = traceId;
        _parentSpanId.Value = parentId;
        _currentSpanId.Value = spanId;

        return new SpanScope(name, traceId, spanId, parentId, priorSpanId, priorParentId, startUtc, attributes);
    }

    /// <summary>
    /// Flush all buffered spans to the gateway's
    /// <c>POST /api/telemetry/spans</c> endpoint. Best-effort; failures
    /// are logged to <c>Console.Error</c> but never thrown — the tracer
    /// must never break customer code paths.
    /// </summary>
    public static async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        List<Dictionary<string, object?>> snapshot;
        lock (_bufferLock)
        {
            if (_buffer.Count == 0) return;
            snapshot = new List<Dictionary<string, object?>>(_buffer);
            _buffer.Clear();
        }

        var resolver = _resolver ?? new CredentialResolver(requireOne: false);
        var apiKey = await resolver.GetAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(apiKey))
            return;  // No key = no flush. Silent because tracing is best-effort.

        // No address = no telemetry, and no guessed host. Spans are dropped
        // rather than posted somewhere the operator did not choose. Unlike the
        // missing key this warns — once — because a tracer that is silently
        // discarding everything looks exactly like one with nothing to send.
        // TryCreate, not new Uri(): Configure() does not validate what it is
        // given, and telemetry must not throw out of a shutdown hook because a
        // customer passed an address without a scheme.
        Uri? configured = null;
        if (!string.IsNullOrEmpty(_gatewayBaseUrl))
            Uri.TryCreate(_gatewayBaseUrl, UriKind.Absolute, out configured);

        var resolved = ShashuEnv.OptionalBaseUrl(configured);
        if (resolved is null)
        {
            if (Interlocked.Exchange(ref _warnedUnconfigured, 1) == 0)
            {
                Console.Error.WriteLine(
                    "[trustgate.tracer] no gateway address configured, so spans are being "
                    + "discarded. Pass gatewayBaseUrl to Tracer.Configure(), or set one of the "
                    + "SHASHU_BASE_URL environment variables. SHASHU is self-hosted: "
                    + "the SDK has no default address and will not guess one.");
            }
            return;
        }
        var baseUrl = resolved.ToString().TrimEnd('/');

        try
        {
            _flushHttp ??= new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            using var content = JsonContent.Create(snapshot);
            using var request = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/api/telemetry/spans")
            {
                Content = content,
            };
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
            using var response = await _flushHttp.SendAsync(request, cancellationToken).ConfigureAwait(false);
            // Don't throw on non-2xx — telemetry is best-effort.
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[trustgate.tracer] flush failed: {ex.Message}");
        }
    }

    /// <summary>Synchronous Flush wrapper for shutdown hooks where async isn't ergonomic.</summary>
    public static void Flush() => FlushAsync().GetAwaiter().GetResult();

    /// <summary>Process-exit hook — registered automatically by Configure().</summary>
    public static void ShutdownHook()
    {
        try { Flush(); } catch { /* best-effort */ }
    }

    /// <summary>
    /// Internal: append a finalised span record to the buffer. Called by
    /// <see cref="SpanScope.Dispose"/>.
    /// </summary>
    internal static void RecordSpan(Dictionary<string, object?> span)
    {
        lock (_bufferLock)
        {
            _buffer.Add(span);
        }
    }

    private static string NewId() => Guid.NewGuid().ToString("N");

    /// <summary>Span lifecycle scope — disposing closes the span.</summary>
    private sealed class SpanScope : IDisposable
    {
        private readonly string _name;
        private readonly string _traceId;
        private readonly string _spanId;
        private readonly string? _parentId;
        private readonly DateTimeOffset _startUtc;
        private readonly IReadOnlyDictionary<string, object?>? _attributes;
        private readonly string? _priorSpanId;
        private readonly string? _priorParentId;
        private bool _disposed;

        public SpanScope(string name, string traceId, string spanId, string? parentId,
                         string? priorSpanId, string? priorParentId,
                         DateTimeOffset startUtc, IReadOnlyDictionary<string, object?>? attributes)
        {
            _name = name;
            _traceId = traceId;
            _spanId = spanId;
            _parentId = parentId;
            _startUtc = startUtc;
            _attributes = attributes;

            // Prior span/parent slot values captured by Span() before it
            // overwrote them; Dispose restores them verbatim. Each scope
            // remembering exactly the state it displaced is what makes
            // arbitrary nesting depth pop correctly without a stack.
            _priorSpanId = priorSpanId;
            _priorParentId = priorParentId;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            var endUtc = DateTimeOffset.UtcNow;
            // Wire shape locked-in by `core/app/api/routers/telemetry.py`'s
            // SpanIngest model: parent_span_id (not parent_id), epoch
            // floats for start_time / end_time (not ISO strings), float
            // `duration` in seconds (not duration_ms). Drift here is
            // silently lossy — the gateway accepts the request with 422
            // for missing fields, but the span never lands in agent_spans.
            var startEpoch = _startUtc.ToUnixTimeMilliseconds() / 1000.0;
            var endEpoch = endUtc.ToUnixTimeMilliseconds() / 1000.0;
            var record = new Dictionary<string, object?>
            {
                ["trace_id"] = _traceId,
                ["span_id"] = _spanId,
                ["parent_span_id"] = _parentId,  // gateway's SpanIngest field name
                ["name"] = _name,
                ["start_time"] = startEpoch,
                ["end_time"] = endEpoch,
                ["duration"] = endEpoch - startEpoch,
                ["status"] = "ok",
            };
            if (_attributes is not null)
            {
                // Gateway's SpanIngest accepts "metadata" as the
                // free-form attribute bag — not "attributes".
                record["metadata"] = _attributes;
            }
            RecordSpan(record);

            // Restore the AsyncLocal stack to whatever it was before the
            // span opened. Critical: when a nested span ends, the OUTER
            // span must remain active.
            Tracer._currentSpanId.Value = _priorSpanId;  // outer span is current again
            Tracer._parentSpanId.Value = _priorParentId; // outer span's parent
            // Trace id is invariant across the span stack within an async flow.
            // When the root span ends, leave _traceId.Value as-is — subsequent
            // top-level spans without an enclosing one will start a fresh trace
            // on their first call to NewId().
            if (_priorSpanId is null)
            {
                Tracer._traceId.Value = null;
            }
        }
    }
}
