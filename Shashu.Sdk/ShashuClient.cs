using Shashu.Audit;
using Shashu.Chat;
using Shashu.Configuration;

namespace Shashu;

/// <summary>
/// Top-level client for Shashu AI. Mirrors the resource-namespaced
/// surface of the OpenAI .NET / Python clients so a port from one to the
/// other is a near-mechanical edit.
/// </summary>
/// <example>
/// <code>
/// using var client = new ShashuClient(new ShashuConfig {
///     ApiKey = Environment.GetEnvironmentVariable("SHASHU_API_KEY"),
/// });
/// var response = await client.Chat.Completions.CreateAsync(new ChatCompletionRequest {
///     Model    = "gpt-4o",
///     Messages = [ new ChatMessage("user", "Hello") ],
/// });
/// Console.WriteLine(response.Choices[0].Message.Content);
/// </code>
/// </example>
public sealed class ShashuClient : IAsyncDisposable, IDisposable
{
    /// <summary>
    /// The csproj <c>&lt;Version&gt;</c>. A surface test holds this string
    /// to that element so the two cannot drift. Used in the default
    /// User-Agent so server-side telemetry can attribute traffic to a
    /// specific SDK version.
    /// </summary>
    public const string SdkVersion = "1.2.0";

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly ShashuConfig _options;
    private readonly ShashuApi _api;
    internal CredentialResolver Resolver { get; }

    /// <summary>Chat-completions resource namespace.</summary>
    public ChatResource Chat { get; }

    /// <summary>
    /// Audit-event ingestion resource (<c>POST /v1/audit/events</c>).
    /// For connector-tier callers pushing vendor LLM events through the
    /// gateway's scanner pipeline.
    /// </summary>
    public AuditResource Audit { get; }

    /// <summary>
    /// Construct a client with explicit options. The most common case in
    /// long-lived services is to use <c>AddShashu(...)</c> on
    /// <c>IServiceCollection</c> instead — that wires up
    /// <see cref="HttpClient"/> through <c>IHttpClientFactory</c>, which
    /// is the .NET-recommended pattern for socket exhaustion safety.
    /// </summary>
    public ShashuClient(ShashuConfig options)
        : this(options, http: null) { }

    /// <summary>
    /// Internal-or-DI constructor that takes a pre-configured HttpClient.
    /// The DI extension uses this path so consumers benefit from
    /// HttpClientFactory pooling.
    /// </summary>
    public ShashuClient(ShashuConfig options, HttpClient? http)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        Resolver = new CredentialResolver(
            apiKey: options.ApiKey,
            tokenProvider: options.TokenProvider,
            requireOne: true);

        var baseUrl = ShashuEnv.RequireBaseUrl(
            options.BaseUrl,
            entryPoint: "new ShashuClient()",
            argument: "ShashuConfig.BaseUrl");

        if (http is null)
        {
            _http = new HttpClient { BaseAddress = baseUrl, Timeout = options.Timeout };
            _ownsHttp = true;
        }
        else
        {
            _http = http;
            _ownsHttp = false;
            if (_http.BaseAddress is null)
                _http.BaseAddress = baseUrl;
        }

        _http.DefaultRequestHeaders.UserAgent.Clear();
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(options.UserAgent);
        if (!string.IsNullOrWhiteSpace(options.OrganizationId))
            _http.DefaultRequestHeaders.Add("X-Org-Id", options.OrganizationId);

        _api = new ShashuApi(_http, _options, Resolver);
        Chat = new ChatResource(_api);
        Audit = new AuditResource(_api);
    }

    /// <summary>
    /// This agent's own id, as known so far — seeded from
    /// <see cref="ShashuConfig.AgentId"/> and refreshed from each gateway
    /// response's <c>x-shashu-agent-id</c> header. Null until known.
    /// </summary>
    public string? AgentId => _api.AgentId;

    /// <summary>
    /// The session this client joins. Minted once at construction when
    /// <see cref="ShashuConfig.SessionId"/> is omitted; a captured
    /// inbound A2A session overrides it, and a per-request
    /// <c>sessionId:</c> is an instruction that wins. See
    /// <c>contracts/sdk-surface.v1.md</c> "Which session a request joins".
    /// </summary>
    public string SessionId => _api.SessionId;

    /// <summary>
    /// Build the lineage headers to stamp on a message THIS agent sends to a DOWNSTREAM
    /// agent (the invoking side, ADR-007 §4.1). <c>caller-agent-id</c> = this client's own
    /// id (the <see cref="ShashuConfig.AgentId"/> option, refreshed from gateway
    /// responses); trace / hop / session default from the tracer so the served flow is
    /// propagated. The downstream agent calls <c>Tracer.CaptureIncoming(...)</c> on these
    /// headers. Throws if the self-id is not known yet — set the <c>AgentId</c> option or
    /// make one gateway call first so the <c>x-shashu-agent-id</c> response header can seed it.
    /// </summary>
    public Dictionary<string, string> BuildInvocationHeaders(
        string? traceId = null, string? sessionId = null, int? hopDepth = null)
    {
        if (string.IsNullOrEmpty(_api.AgentId))
        {
            throw new InvalidOperationException(
                "Self agent id is unknown — set ShashuConfig.AgentId or make one "
                + "gateway call first so the x-shashu-agent-id response header can seed it.");
        }
        return Context.WorkflowContext.BuildInvocationHeaders(
            _api.AgentId!, traceId, sessionId, hopDepth);
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        if (_ownsHttp)
            _http.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_ownsHttp)
            _http.Dispose();
    }
}
