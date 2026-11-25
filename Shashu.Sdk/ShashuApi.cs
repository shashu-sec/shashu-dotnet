using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Shashu.Chat;
using Shashu.Context;
using Shashu.Tracing;

namespace Shashu;

/// <summary>
/// Internal HTTP plumbing shared across resource classes (Chat, Audit,
/// etc.). Centralises retry policy, JSON serialisation, and Shashu's
/// error-shape parsing so the per-resource code stays focused on
/// request shaping.
/// </summary>
internal sealed class ShashuApi
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly ShashuConfig _options;
    private readonly CredentialResolver _resolver;

    /// <summary>
    /// This agent's OWN id (ADR-007 §4.1). Seeded from <see cref="ShashuConfig.AgentId"/>
    /// and refreshed from each gateway response's <c>x-shashu-agent-id</c> header (read on
    /// EVERY response, including non-2xx). Used by <see cref="ShashuClient.BuildInvocationHeaders"/>
    /// to stamp caller = self on a downstream invocation — never sent as the gateway call's
    /// caller. Advisory lineage, never auth.
    /// </summary>
    internal string? AgentId { get; private set; }

    /// <summary>
    /// Construction-time session default. Minted once when
    /// <see cref="ShashuConfig.SessionId"/> is omitted.
    /// </summary>
    internal string SessionId { get; }

    public ShashuApi(HttpClient http, ShashuConfig options, CredentialResolver resolver)
    {
        _http = http;
        _options = options;
        _resolver = resolver;
        AgentId = options.AgentId;
        SessionId = string.IsNullOrWhiteSpace(options.SessionId)
            ? Guid.NewGuid().ToString("N")
            : options.SessionId.Trim();
    }

    private async Task ApplyCredentialAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = await _resolver.GetAsync(cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    /// <summary>
    /// Cache this agent's own id from the gateway's <c>x-shashu-agent-id</c> response
    /// header, when present. Called on EVERY response (success + error) so even a blocked
    /// response seeds the self-id for the next A2A invocation. Fail-soft: never throws.
    /// </summary>
    private void CaptureAgentId(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("x-shashu-agent-id", out var values))
        {
            var aid = values.FirstOrDefault();
            if (!string.IsNullOrEmpty(aid))
                AgentId = aid;
        }
    }

    /// <summary>
    /// POST a JSON body and stream Server-Sent Events back as a typed
    /// async stream. Used by <c>chat.completions.CreateStreamAsync</c>.
    /// </summary>
    /// <remarks>
    /// <para>SSE framing: <c>data: &lt;json&gt;\n\n</c> per chunk; the
    /// terminal sentinel is <c>data: [DONE]</c>. Mid-stream errors from
    /// the gateway (e.g. egress scanner blocks) surface as JSON objects
    /// with an <c>error</c> field; we map those to
    /// <see cref="ShashuException"/>.</para>
    ///
    /// <para>HTTP-level retry is intentionally NOT applied here — once
    /// the stream has started, replaying would mean the customer
    /// receives parts of two responses concatenated. Callers wanting
    /// retry must restart the call themselves.</para>
    /// </remarks>
    public async IAsyncEnumerable<TChunk> PostStreamAsync<TRequest, TChunk>(
        string path, TRequest body,
        [EnumeratorCancellation] CancellationToken cancellationToken,
        string? sessionId = null)
        where TChunk : class
    {
        HttpResponseMessage? response = null;
        var passRetried = false;
        while (true)
        {
            using var content = JsonContent.Create(body, options: Json);
            using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = content };
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            ApplyWorkflowContext(request, sessionId);
            await ApplyCredentialAsync(request, cancellationToken).ConfigureAwait(false);

            response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            CaptureAgentId(response);

            if (response.IsSuccessStatusCode)
                break;

            var raw = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            var code = CredentialResolver.PassRefusalCode(status, raw);
            if (!passRetried && CredentialResolver.ShouldRetryPassRefused(
                    _resolver.UsesProvider, status, code, bytesDelivered: false))
            {
                response.Dispose();
                _resolver.Invalidate();
                passRetried = true;
                continue;
            }
            var headers = CaptureShashuHeaders(response);
            response.Dispose();
            throw ShashuException.FromResponse(status, raw, headers);
        }

        using var owned = response!;
        await using var stream = await owned.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        // SSE parser: collect lines per frame (separated by blank line),
        // pull `data:` lines out, deserialise. Compatible with both
        // OpenAI's framing and the gateway's pass-through.
        var dataBuffer = new StringBuilder();
        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                break;
            }

            if (line.Length == 0)
            {
                // Blank line = end of frame — flush whatever we collected.
                if (dataBuffer.Length == 0) continue;

                var payload = dataBuffer.ToString();
                dataBuffer.Clear();

                if (payload == "[DONE]")
                {
                    yield break;
                }

                TChunk? parsed;
                try
                {
                    parsed = JsonSerializer.Deserialize<TChunk>(payload, Json);
                }
                catch (JsonException ex)
                {
                    throw new ShashuException($"Failed to parse SSE chunk: {ex.Message}");
                }
                if (parsed is not null)
                {
                    if (parsed is ChatCompletionChunk chunk)
                        ThrowIfStreamBlocked(chunk);
                    yield return parsed;
                }
            }
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                // Strip `data:` prefix + at most one space (per SSE spec).
                var value = line.Length > 5 && line[5] == ' '
                    ? line.Substring(6)
                    : line.Substring(5);
                if (dataBuffer.Length > 0) dataBuffer.Append('\n');
                dataBuffer.Append(value);
            }
            // `event:` / `id:` / `retry:` / `:` (comment) lines are ignored.
        }
    }

    /// <summary>
    /// Convenience overload for the chat-completions streaming path —
    /// callers don't have to specify both type parameters.
    /// </summary>
    internal IAsyncEnumerable<ChatCompletionChunk> PostStreamAsync(
        string path, ChatCompletionRequest body, CancellationToken cancellationToken, string? sessionId = null) =>
        PostStreamAsync<ChatCompletionRequest, ChatCompletionChunk>(
            path, body, cancellationToken, sessionId);

    /// <summary>
    /// Apply the per-request workflow-context headers (Shadow AI source,
    /// trace id, runtime metadata, source-tool blob) to a request just
    /// before it goes on the wire. Mirrors the Python SDK's
    /// <c>build_trustgate_headers()</c> contract.
    /// </summary>
    /// <remarks>
    /// Keeps the heavy-lifting in <see cref="WorkflowContext.Build"/> so the
    /// same code can be reused by <c>GatewayRouting</c>'s routing handler
    /// when it intercepts vendor-SDK traffic.
    /// </remarks>
    private void ApplyWorkflowContext(HttpRequestMessage request, string? requestSessionId = null)
    {
        foreach (var (k, v) in WorkflowContext.Build(sessionId: ResolveSession(requestSessionId)))
        {
            // Header may already be present (e.g. caller-supplied via
            // RequestOptions in a future revision). Caller wins.
            if (!request.Headers.Contains(k))
                request.Headers.TryAddWithoutValidation(k, v);
        }
        ApplyContractVersion(request);
    }

    /// <summary>
    /// Declare the contract version, as the Python SDK does. Absent, the
    /// gateway serves its legacy contract: a refusal is a 200 reply and the
    /// body carries no decision. Skipped when the caller opted out
    /// (<see cref="ShashuConfig.ContractVersion"/> null) or already
    /// set it, on the request or on their own <see cref="HttpClient"/>'s
    /// defaults — a per-request header would silently override those.
    /// </summary>
    private void ApplyContractVersion(HttpRequestMessage request)
    {
        var version = _options.ContractVersion;
        if (string.IsNullOrWhiteSpace(version)
            || request.Headers.Contains(ContractVersionHeader)
            || _http.DefaultRequestHeaders.Contains(ContractVersionHeader))
            return;
        request.Headers.TryAddWithoutValidation(ContractVersionHeader, version);
    }

    /// <summary>The request header <see cref="ShashuConfig.ContractVersion"/> is sent under.</summary>
    internal const string ContractVersionHeader = "shashu-version";

    /// <summary>
    /// Per-request is an instruction that wins; a captured inbound session
    /// overrides the construction-time default; otherwise the minted/passed
    /// client session. One place, matching
    /// <c>contracts/sdk-surface.v1.md</c>.
    /// </summary>
    private string ResolveSession(string? requestSessionId)
    {
        if (!string.IsNullOrWhiteSpace(requestSessionId))
            return requestSessionId.Trim();
        if (Tracer.IsA2aHop && !string.IsNullOrWhiteSpace(Tracer.SessionId))
            return Tracer.SessionId;
        return SessionId;
    }

    public async Task<TResponse> PostAsync<TRequest, TResponse>(
        string path, TRequest body, CancellationToken cancellationToken, string? sessionId = null)
        where TResponse : class
    {
        var attempt = 0;
        var passRetried = false;
        var payload = JsonSerializer.SerializeToUtf8Bytes(body, Json);
        var frozenContext = WorkflowContext.Build(sessionId: ResolveSession(sessionId));
        while (true)
        {
            using var content = new ByteArrayContent(payload);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = content };
            foreach (var (k, v) in frozenContext)
            {
                if (!request.Headers.Contains(k))
                    request.Headers.TryAddWithoutValidation(k, v);
            }
            ApplyContractVersion(request);
            await ApplyCredentialAsync(request, cancellationToken).ConfigureAwait(false);

            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException) when (attempt < _options.MaxRetries)
            {
                // Connection-level failure — the request never got a response, so a
                // retry can't duplicate a side effect. (Mirrors the Python/Node SDKs.)
                await Task.Delay(BackoffMs(attempt), cancellationToken).ConfigureAwait(false);
                attempt++;
                continue;
            }

            using (response)
            {
                // Cache self-id from EVERY response (incl. non-2xx) before any throw, so a
                // blocked response still seeds caller-agent-id for the next A2A invocation.
                CaptureAgentId(response);

                if (response.IsSuccessStatusCode)
                {
                    var raw = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(raw))
                        throw new ShashuException("Server returned an empty body where a JSON object was expected.");
                    var parsed = JsonSerializer.Deserialize<TResponse>(raw, Json)
                        ?? throw new ShashuException("Server returned an empty body where a JSON object was expected.");
                    var headers = CaptureShashuHeaders(response);
                    if (parsed is ChatCompletionResponse chat)
                    {
                        chat.ShashuHeaders = headers;
                        ThrowIfCompatibleBlock(chat, raw, headers);
                    }
                    return parsed;
                }

                var status = (int)response.StatusCode;
                var rawError = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var passCode = CredentialResolver.PassRefusalCode(status, rawError);
                if (!passRetried && CredentialResolver.ShouldRetryPassRefused(
                        _resolver.UsesProvider, status, passCode, bytesDelivered: false))
                {
                    _resolver.Invalidate();
                    passRetried = true;
                    continue;
                }

                // Conservative retry policy (see ShashuConfig.MaxRetries):
                // retry 429 only. 5xx is NOT retried by default — the gateway already
                // retries upstream providers; LegacyRetryPolicy restores 5xx retries.
                var is5xx = status >= 500 && status < 600;
                var invited = TruthyHeader(response, "x-should-retry")
                    || TryGetRetryAfterMs(response, out _);
                var busy = (status == 429 || status == 503) && invited;
                var retryable = busy || (_options.LegacyRetryPolicy && is5xx && status != 503);

                if (_options.MaxRetries <= 0 || !retryable || attempt >= _options.MaxRetries)
                {
                    throw ShashuException.FromResponse(status, rawError, CaptureShashuHeaders(response));
                }

                int delayMs;
                if (busy && !_options.LegacyRetryPolicy && TryGetRetryAfterMs(response, out var retryAfterMs))
                    delayMs = WithJitter(retryAfterMs);
                else
                    delayMs = WithJitter(BackoffMs(attempt));

                await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
                attempt++;
            }
        }
    }

    private static Dictionary<string, string> CaptureShashuHeaders(HttpResponseMessage response)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in response.Headers)
        {
            var name = header.Key;
            if (name.StartsWith("x-shashu-", StringComparison.OrdinalIgnoreCase)
                || name.Equals("x-should-retry", StringComparison.OrdinalIgnoreCase)
                || name.Equals("x-request-id", StringComparison.OrdinalIgnoreCase))
            {
                headers[name] = string.Join(",", header.Value);
            }
        }
        return headers;
    }

    private static void ThrowIfCompatibleBlock(ChatCompletionResponse chat, string raw, IReadOnlyDictionary<string, string> headers)
    {
        if (chat.Choices.Any(c => c.FinishReason == "content_filter"))
            throw ShashuContentFilterException.FromCompatible(raw, headers);
    }

    private static void ThrowIfStreamBlocked(ChatCompletionChunk chunk)
    {
        if (chunk.Choices.Any(c => c.FinishReason == "content_filter"))
            throw ShashuContentFilterException.FromStreamChunk();
    }

    /// <summary>Exponential backoff with jitter: 250ms, 500ms, 1s, capped at 2s.</summary>
    private static int BackoffMs(int attempt) =>
        Math.Min(2000, 250 * (1 << attempt)) + Random.Shared.Next(0, 100);

    private static bool TruthyHeader(HttpResponseMessage response, string name)
    {
        if (!response.Headers.TryGetValues(name, out var values))
            return false;
        var raw = string.Join(",", values).Trim();
        return raw.Equals("1", StringComparison.OrdinalIgnoreCase)
            || raw.Equals("true", StringComparison.OrdinalIgnoreCase)
            || raw.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || raw.Equals("on", StringComparison.OrdinalIgnoreCase);
    }

    private static int WithJitter(int millis)
    {
        var capped = Math.Min(30_000, Math.Max(0, millis));
        var extra = (int)(capped * 0.2 * Random.Shared.NextDouble());
        return Math.Min(30_000, capped + extra);
    }

    /// <summary>
    /// Parse the <c>Retry-After</c> header (delta-seconds or HTTP-date) into a
    /// delay in milliseconds, clamped to [0, 30s]. Returns false if absent/unparseable.
    /// </summary>
    private static bool TryGetRetryAfterMs(HttpResponseMessage response, out int delayMs)
    {
        delayMs = 0;
        var ra = response.Headers.RetryAfter;
        if (ra is null) return false;
        double ms;
        if (ra.Delta is TimeSpan delta)
            ms = delta.TotalMilliseconds;
        else if (ra.Date is DateTimeOffset date)
            ms = (date - DateTimeOffset.UtcNow).TotalMilliseconds;
        else
            return false;
        delayMs = (int)Math.Clamp(ms, 0, 30_000);
        return true;
    }
}

/// <summary>
/// Thrown when the gateway returns a non-success status code. Wraps the
/// raw response so callers can inspect the Shashu-shape error body.
/// </summary>
/// <remarks>
/// Two responses raise a more-specific subclass — callers can catch the parent
/// for generic handling or the subclass for specific UX:
/// <list type="bullet">
///   <item><b>403</b> → <see cref="ShashuPolicyViolation"/>, an
///         <em>access</em> decision (model allow-list, budget, tool, loop
///         guardrail).</item>
///   <item><b>400 with <c>error.type == "content_filter"</c></b> →
///         <see cref="ShashuContentFilterException"/>, a <em>content</em>
///         block under the gateway's <c>explicit</c> encoding. It is NOT a
///         malformed request and must not be reported as one.</item>
/// </list>
///
/// A security block on the <c>compatible</c> / legacy encoding is the same
/// exception: it arrives as HTTP 200 with
/// <c>finish_reason == "content_filter"</c> and raises
/// <see cref="ShashuContentFilterException"/>. See that type.
///
/// Any other refusal the gateway serves under the contract version (a 402
/// plan limit, a 429 quota, a 503 when an inspection could not finish) is this
/// base type, with the gateway's plain-English sentence as its
/// <see cref="Exception.Message"/>.
/// </remarks>
public class ShashuException : Exception
{
    /// <summary>HTTP status code returned by the gateway.</summary>
    public int StatusCode { get; }

    /// <summary>Raw response body as returned by the gateway.</summary>
    public string ResponseBody { get; }

    /// <summary>
    /// <c>x-shashu-*</c> (and sibling) response headers, when captured.
    /// Absence is today's behaviour — the dictionary is empty, not null.
    /// </summary>
    public IReadOnlyDictionary<string, string> ResponseHeaders { get; protected set; }
        = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Why a route was refused for the data in the request, off the decision;
    /// null when the response carried none. Set on a <c>route_data_policy</c>
    /// refusal, so an app can say which route is not approved for which
    /// category without parsing the message.
    /// </summary>
    public RoutePolicy? RoutePolicy => Shashu.DecisionOf(this)?.RoutePolicy;

    /// <summary>Subclass constructor. Status and body are stored for <see cref="Shashu.DecisionOf"/>.</summary>
    protected ShashuException(int statusCode, string responseBody, string message)
        : base(message)
    {
        StatusCode = statusCode;
        ResponseBody = responseBody;
    }

    internal ShashuException(string message) : base(message)
    {
        StatusCode = 0;
        ResponseBody = string.Empty;
    }

    internal ShashuException(string message, Exception inner) : base(message, inner)
    {
        StatusCode = 0;
        ResponseBody = string.Empty;
    }

    /// <summary>
    /// Map an HTTP error response to the most-specific
    /// <see cref="ShashuException"/> subclass. Currently:
    /// <list type="bullet">
    ///   <item>403 → <see cref="ShashuPolicyViolation"/>, with
    ///         <c>error.message</c> parsed from the JSON body. Note a 403 can be
    ///         a gateway access decision OR a provider permission failure passed
    ///         through — see that type's remarks.</item>
    ///   <item>400 carrying <c>error.type == "content_filter"</c> →
    ///         <see cref="ShashuContentFilterException"/>.</item>
    ///   <item>everything else → <see cref="ShashuException"/>, whose
    ///         message is the gateway's <c>error.message</c> when the body
    ///         carries the decision (<c>error.shashu</c>), and
    ///         "Shashu request failed with HTTP n: …" otherwise.</item>
    /// </list>
    ///
    /// <para><b>Dispatch is on the TRANSPORT STATUS, not on <c>error.code</c>.</b>
    /// That field is a §3 reason (<c>jailbreak</c>, <c>dlp</c>,
    /// <c>egress_denied</c>, <c>auth_expired</c>, …) on every path served under
    /// contract version <c>2026-09-01</c> (which this SDK declares by default);
    /// the HTTP status as a string (<c>"403"</c>) on the legacy contract; the literal <c>"invalid_request"</c>
    /// on a schema-invalid body; and absent altogether on the 500 crash handler
    /// and the Anthropic-shaped <c>/v1/messages</c> envelope. Only the second of
    /// those four is a status, and where it IS a status it always equals the
    /// transport status — so reading it could only ever introduce a
    /// disagreement, never resolve one. The status is the one value present on
    /// every response.</para>
    ///
    /// <para><c>error.type</c> is read for exactly one value,
    /// <c>"content_filter"</c>, and only on a 400. Every other 4xx the gateway
    /// raises itself carries a value that merely restates the status
    /// (<c>permission_error</c> on a 403, <c>authentication_error</c> on a 401)
    /// under the contract this SDK declares by default, and
    /// <c>"invalid_request_error"</c> on the legacy contract — neither refines
    /// what the status already said.</para>
    /// </summary>
    internal static ShashuException FromResponse(
        int statusCode, string body, IReadOnlyDictionary<string, string>? headers = null)
    {
        ShashuException ex;
        if (statusCode == 401)
        {
            var message = CredentialResolver.MessageFromBody(body)
                ?? $"Shashu request failed with HTTP {statusCode}: {Truncate(body, 256)}";
            ex = new ShashuAuthenticationException(
                statusCode, body, message, CredentialResolver.CodeFromBody(body));
        }
        else if (statusCode == 403)
        {
            ex = ShashuPolicyViolation.FromBody(body);
        }
        else if (statusCode == 400 && IsContentFilter(body))
        {
            ex = ShashuContentFilterException.FromBody(statusCode, body);
        }
        else
        {
            var message = ContractMessage(body)
                ?? $"Shashu request failed with HTTP {statusCode}: {Truncate(body, 256)}";
            ex = new ShashuException(statusCode, body, message);
        }
        if (headers is not null)
            ex.ResponseHeaders = headers;
        return ex;
    }

    /// <summary>
    /// True when the body is the gateway's <c>explicit</c>-encoding content
    /// block: <c>error.type == "content_filter"</c>.
    /// </summary>
    /// <remarks>
    /// Fails CLOSED in the direction that matters. An unparseable body, a
    /// missing <c>error</c> object or any other <c>type</c> returns false, so
    /// the response stays a plain <see cref="ShashuException"/> on its real
    /// status — a 400 that is genuinely a malformed request is never reported
    /// as a block. The reverse mistake (a block reported as a malformed
    /// request) is the defect this method exists to end, and it needs a
    /// positive <c>"content_filter"</c> to avoid.
    /// </remarks>
    private static bool IsContentFilter(string body)
    {
        if (string.IsNullOrEmpty(body))
        {
            return false;
        }
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("error", out var errEl)
                && errEl.ValueKind == JsonValueKind.Object
                && errEl.TryGetProperty("type", out var typeEl)
                && typeEl.ValueKind == JsonValueKind.String
                && typeEl.GetString() == "content_filter";
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// The plain-English sentence of a gateway refusal served under a contract
    /// version: <c>error.message</c>, but only when <c>error.shashu</c> is
    /// there to say the gateway wrote it (a 402 plan limit, a 429 quota, a 503
    /// fail-closed inspection, …). Anything else keeps the raw-body message,
    /// so a provider or proxy error is never dressed up as ours.
    /// </summary>
    private static string? ContractMessage(string body)
    {
        if (string.IsNullOrEmpty(body))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("error", out var errEl)
                && errEl.ValueKind == JsonValueKind.Object
                && errEl.TryGetProperty("shashu", out var tg)
                && tg.ValueKind == JsonValueKind.Object
                && errEl.TryGetProperty("message", out var msgEl)
                && msgEl.ValueKind == JsonValueKind.String
                && msgEl.GetString() is { Length: > 0 } msg)
            {
                return msg;
            }
        }
        catch (JsonException)
        {
        }
        return null;
    }

    internal static string Truncate(string s, int max) =>
        s.Length <= max ? s : string.Concat(s.AsSpan(0, max), "...");
}

/// <summary>
/// Specialised <see cref="ShashuException"/> raised when the gateway
/// returns HTTP 403. Parses the gateway's error body to surface the
/// human-readable reason via <see cref="Exception.Message"/>.
/// </summary>
/// <remarks>
/// <para><b>What a 403 actually is.</b> An <em>access</em> decision: the
/// request was refused before the model was ever asked. Three different
/// producers emit one, and they are not interchangeable:</para>
/// <list type="number">
///   <item><b>A gateway access decision</b> — model not in the agent's allowed
///         routes, monthly budget exhausted, tool denied, loop guardrail
///         tripped. This is the case the class is named for.</item>
///   <item><b>A provider permission failure, passed through</b> — the stored
///         provider credential is valid but has no access to the target model.
///         Nothing about the customer's Shashu policy is wrong. Carries
///         <c>error.type</c> = <c>"ProviderPermissionError"</c> and a message
///         prefixed <c>"Shashu Provider Error: "</c>.</item>
///   <item><b>An Anthropic-shaped envelope</b> — on <c>/v1/messages</c> with
///         <c>TRUSTGATE_WIRE_SHAPE_MESSAGES=enforce</c>, the gateway rewrites
///         access-decision 403s into Anthropic's error shape and
///         <see cref="ErrorType"/> reads <c>"permission_error"</c>.</item>
/// </list>
/// <para><b>A security block is NOT a 403.</b> Catching this exception does not
/// catch a DLP, prompt-injection or content-scan block. A block has two
/// encodings, and neither of them is a 403:</para>
/// <list type="bullet">
///   <item><b><c>explicit</c></b> (the default at contract <c>2026-09-01</c>) —
///         <b>HTTP 400</b> with <c>error.type == "content_filter"</c>, raised as
///         <see cref="ShashuContentFilterException"/>.</item>
///   <item><b><c>compatible</c> / legacy</b> — <b>HTTP 200</b> carrying the
///         refusal, with the verdict in <c>finish_reason == "content_filter"</c>
///         rather than the status code. Raised as
///         <see cref="ShashuContentFilterException"/> so a caller who
///         does not inspect <c>FinishReason</c> cannot treat a block as an
///         answer.</item>
/// </list>
/// <para>A caller that handles only this exception will hand a refusal
/// downstream as if it were the model's answer. All three branches are
/// required:</para>
/// <code>
/// try {
///     var response = await client.Chat.Completions.CreateAsync(request);
///     var choice   = response.Choices[0];
///     Use(choice.Message.Content);
/// } catch (ShashuContentFilterException ex) {     // 200 or 400 — a block
///     HandleBlocked(ex.Message);                     // ex.Code names the finding when present
/// } catch (ShashuPolicyViolation ex) {            // 403 — access decision
///     HandleDenied(ex.Message);
/// } catch (ShashuException ex) when (ex.StatusCode >= 500) {
///     // Network / upstream issue; retry.
/// }
/// </code>
/// <para><b>The legacy bodies, as captured from a live gateway (2026-09-01)
/// before this SDK declared a contract version.</b> They are what a client
/// with <see cref="ShashuConfig.ContractVersion"/> set to null still
/// receives. Under the contract version this SDK sends by default the same
/// refusal carries <c>"type": "permission_error"</c>, a reason in
/// <c>"code"</c> (<c>feature_disabled</c>, <c>tool_policy</c>, …) and the
/// decision nested in <c>error.shashu</c>. Access decision on <c>/v1/chat/completions</c>, <c>/v1/completions</c>,
/// <c>/v1/responses</c> and <c>/v1/conversations</c>:</para>
/// <code>
/// {
///   "error": {
///     "message": "Access Denied: Model 'claude-3-opus-20240229' is not in the allowed routes for this agent.",
///     "type": "invalid_request_error",
///     "param": null,
///     "code": "403"
///   },
///   "choices": []
/// }
/// </code>
/// <para>The same decision on <c>/v1/messages</c> with the Messages wire shape
/// enforced:</para>
/// <code>
/// { "type": "error", "error": { "type": "permission_error", "message": "Access Denied: ..." } }
/// </code>
/// <para><b><see cref="ErrorType"/> is not a policy discriminator.</b> Under
/// the contract version it restates the status (<c>"permission_error"</c> on
/// every 403); on the legacy contract it is <c>"invalid_request_error"</c>
/// for every 4xx the gateway raises itself — a 401, a 403 and a 422 are
/// indistinguishable by it. Branch on <see cref="ShashuException.StatusCode"/>. Sub-typing
/// budget-vs-policy is tracked as <c>TRUSTGATE-403-DISCRIMINATOR</c> and is not
/// available today — <see href="https://github.com/aisquad001/trustgate-ai/issues/736">trustgate-ai#736</see>.</para>
/// </remarks>
public class ShashuPolicyViolation : ShashuException
{
    /// <summary>The <c>error.type</c> field from the gateway's response, if present.</summary>
    public string? ErrorType { get; }

    private protected ShashuPolicyViolation(int statusCode, string responseBody, string message, string? errorType)
        : base(statusCode, responseBody, message)
    {
        ErrorType = errorType;
    }

    internal static ShashuPolicyViolation FromBody(string body)
    {
        var message = "Shashu policy violation (403)";
        string? errorType = null;
        var memoryWriteBlocked = false;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            // A refused memory write is the one 403 with its own type, read off
            // the decision's memory block rather than off `code`.
            memoryWriteBlocked = Shashu.DecisionFrom(doc.RootElement, headers: null) is { MemoryBlocked: > 0 };
            if (doc.RootElement.TryGetProperty("error", out var errEl))
            {
                if (errEl.TryGetProperty("message", out var msgEl)
                    && msgEl.ValueKind == System.Text.Json.JsonValueKind.String
                    && msgEl.GetString() is { Length: > 0 } msg)
                {
                    message = msg;
                }
                if (errEl.TryGetProperty("type", out var typeEl)
                    && typeEl.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    errorType = typeEl.GetString();
                }
            }
            else if (doc.RootElement.TryGetProperty("message", out var topMsgEl)
                && topMsgEl.ValueKind == System.Text.Json.JsonValueKind.String
                && topMsgEl.GetString() is { Length: > 0 } topMsg)
            {
                message = topMsg;
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // Body wasn't valid JSON — keep the default message.
        }

        return memoryWriteBlocked
            ? new MemoryWriteBlocked(403, body, message, errorType)
            : new ShashuPolicyViolation(403, body, message, errorType);
    }
}

/// <summary>403 — the gateway refused to store a memory write.</summary>
/// <remarks>
/// A <see cref="ShashuPolicyViolation"/>, so every existing <c>catch</c> for
/// that type still catches it. Thrown when the decision's <c>memory</c> block
/// reports a blocked write; which write, and why, is on <see cref="Memory"/>.
/// The type is chosen from that block, never from a copy of the <c>code</c> or
/// reason vocabulary.
/// </remarks>
public sealed class MemoryWriteBlocked : ShashuPolicyViolation
{
    internal MemoryWriteBlocked(int statusCode, string responseBody, string message, string? errorType)
        : base(statusCode, responseBody, message, errorType)
    {
    }

    /// <summary>The decision's memory block.</summary>
    public MemoryActivity? Memory => Shashu.DecisionOf(this)?.Memory;
}

/// <summary>
/// The gateway blocked the content. ONE type, both encodings.
/// </summary>
/// <remarks>
/// <para><b>Two encodings, one event.</b> A security block reaches the caller
/// in one of two shapes, and which one depends on a request header
/// (<c>Shashu-Block-Encoding</c>) or an account default the caller may not
/// control:</para>
/// <list type="bullet">
///   <item><b><c>explicit</c></b> (the default at contract <c>2026-09-01</c>,
///         which this SDK declares by default through
///         <see cref="ShashuConfig.ContractVersion"/>) —
///         <b>HTTP 400</b>, <c>error.type == "content_filter"</c> and
///         <c>error.code</c> naming the finding. This exception.</item>
///   <item><b><c>compatible</c> / legacy</b> — <b>HTTP 200</b>,
///         <c>finish_reason == "content_filter"</c> on the choice. Served on
///         the <c>compatible</c> encoding, or on the legacy contract when
///         <see cref="ShashuConfig.ContractVersion"/> is null. This
///         exception too.</item>
/// </list>
///
/// <para><b>Why this type exists.</b> The 400 encoding used to raise a bare
/// <see cref="ShashuException"/> carrying "Shashu request failed with
/// HTTP 400", which is indistinguishable from a malformed request — so a
/// caller with correct code was pointed at a payload that was never the
/// problem, and the block never reached whatever handles blocks. A refusal must
/// not present as the caller's mistake.</para>
///
/// <para><b>The body, as the gateway's own builders emit it:</b></para>
/// <code>
/// {
///   "error": {
///     "message": "I cannot fulfill this request because it violates the organization's security policy.",
///     "type": "content_filter",
///     "param": null,
///     "code": "jailbreak",
///     "shashu": { "decision": "block", "code": "jailbreak", ... }
///   },
///   "choices": []
/// }
/// </code>
///
/// <para><see cref="Code"/> is the §3 finding: <c>jailbreak</c>, <c>dlp</c>,
/// <c>pii_output</c>, <c>secret</c> or <c>prompt_injection</c>. It is the field
/// that says WHICH detector refused, and it is why <c>error.code</c> is no
/// longer the HTTP status restated.</para>
/// </remarks>
public sealed class ShashuContentFilterException : ShashuException
{
    /// <summary>The <c>error.type</c> field from the gateway's response, if present.</summary>
    public string? ErrorType { get; }

    /// <summary>The §3 finding from <c>error.code</c> — <c>jailbreak</c>, <c>dlp</c>, … — or null.</summary>
    public string? Code { get; }

    private ShashuContentFilterException(
        int statusCode, string responseBody, string message, string? errorType, string? code)
        : base(statusCode, responseBody, message)
    {
        ErrorType = errorType;
        Code = code;
    }

    internal static ShashuContentFilterException FromBody(int statusCode, string body)
    {
        var message = "Shashu blocked this request's content";
        string? errorType = null;
        string? code = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var errEl)
                && errEl.ValueKind == JsonValueKind.Object)
            {
                if (errEl.TryGetProperty("message", out var msgEl)
                    && msgEl.ValueKind == JsonValueKind.String
                    && msgEl.GetString() is { Length: > 0 } msg)
                {
                    message = msg;
                }
                if (errEl.TryGetProperty("type", out var typeEl)
                    && typeEl.ValueKind == JsonValueKind.String)
                {
                    errorType = typeEl.GetString();
                }
                if (errEl.TryGetProperty("code", out var codeEl)
                    && codeEl.ValueKind == JsonValueKind.String)
                {
                    code = codeEl.GetString();
                }
            }
        }
        catch (JsonException)
        {
            // Body wasn't valid JSON — keep the default message. Never a reason
            // to downgrade the exception TYPE: the status and error.type already
            // said this was a block.
        }

        return new ShashuContentFilterException(statusCode, body, message, errorType, code);
    }

    /// <summary>
    /// The <c>compatible</c> / legacy encoding: HTTP 200,
    /// <c>finish_reason == "content_filter"</c>. Same exception as the 400.
    /// </summary>
    internal static ShashuContentFilterException FromCompatible(
        string body, IReadOnlyDictionary<string, string>? headers = null)
    {
        var message = "Shashu blocked this request's content";
        string? code = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("choices", out var choices)
                && choices.ValueKind == JsonValueKind.Array
                && choices.GetArrayLength() > 0)
            {
                var choice = choices[0];
                if (choice.TryGetProperty("message", out var msg)
                    && msg.ValueKind == JsonValueKind.Object
                    && msg.TryGetProperty("content", out var content)
                    && content.ValueKind == JsonValueKind.String
                    && content.GetString() is { Length: > 0 } text)
                {
                    message = text;
                }
            }
            if (doc.RootElement.TryGetProperty("shashu", out var tg)
                && tg.ValueKind == JsonValueKind.Object
                && tg.TryGetProperty("code", out var codeEl)
                && codeEl.ValueKind == JsonValueKind.String)
            {
                code = codeEl.GetString();
            }
        }
        catch (JsonException)
        {
            // Keep the default message. The finish_reason already said this was a block.
        }

        var ex = new ShashuContentFilterException(200, body, message, "content_filter", code);
        if (headers is not null)
            ex.ResponseHeaders = headers;
        return ex;
    }

    /// <summary>
    /// A streamed <c>finish_reason == "content_filter"</c> chunk. Same event
    /// as <see cref="FromCompatible"/>; the body is the stream, not a JSON
    /// object we can re-parse for the sentence.
    /// </summary>
    internal static ShashuContentFilterException FromStreamChunk() =>
        new(200, string.Empty, "Shashu blocked this request's content", "content_filter", null);
}
