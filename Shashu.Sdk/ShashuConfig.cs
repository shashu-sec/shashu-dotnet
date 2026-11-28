namespace Shashu;

/// <summary>
/// Configuration for <see cref="ShashuClient"/>. Mirrors the Python
/// SDK's <c>Shashu(...)</c> constructor surface so cross-language
/// migration is mechanical.
/// </summary>
public sealed class ShashuConfig
{
    /// <summary>
    /// API key for the agent (<c>shashu_sk_...</c>). If unset when constructing a
    /// client, <c>SHASHU_API_KEY</c> is read, then <c>SHASHU_API_KEY</c>
    /// (warns once). Give either this or <see cref="TokenProvider"/>, not both.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Cloud pass: a function that returns the current pass. SHASHU never
    /// calls Entra, Google or AWS. Exactly one of <see cref="ApiKey"/> or this.
    /// </summary>
    public Func<CancellationToken, ValueTask<string>>? TokenProvider { get; set; }

    /// <summary>
    /// Base URL of your gateway. Falls back to <c>SHASHU_BASE_URL</c>, then
    /// <c>SHASHU_BASE_URL</c> (warns once).
    /// </summary>
    /// <remarks>
    /// There is no default and the SDK will not guess one — Shashu is
    /// self-hosted, so a vendor-hostname fallback would send a misconfigured
    /// process's prompts to a host nobody in your organisation chose. When
    /// neither this nor the environment variable resolves,
    /// <see cref="ShashuClient"/>'s constructor throws
    /// <see cref="ShashuConfigException"/>.
    /// </remarks>
    public Uri? BaseUrl { get; set; }

    /// <summary>
    /// Per-request timeout. Defaults to 60s — matches the Python SDK.
    /// </summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Maximum retries on transient failures. Defaults to 2. Set to 0 to
    /// disable retries entirely.
    /// </summary>
    /// <remarks>
    /// By default the SDK retries only connection-level failures and HTTP 429
    /// (honoring the <c>Retry-After</c> header). It does NOT retry 5xx, because
    /// the gateway already retries upstream providers (with backoff + circuit
    /// breakers); SDK-side 5xx retries would stack on top and amplify load during
    /// an incident. Set <see cref="LegacyRetryPolicy"/> to restore 5xx retries.
    /// </remarks>
    public int MaxRetries { get; set; } = 2;

    /// <summary>
    /// Opt back in to the pre-fix retry behavior: also retry 5xx responses and
    /// ignore the <c>Retry-After</c> header (use exponential backoff for 429).
    /// Defaults to <c>false</c> (the conservative policy). Provided as a
    /// temporary escape hatch for callers who relied on the SDK swallowing
    /// transient 5xx; prefer leaving it off.
    /// </summary>
    public bool LegacyRetryPolicy { get; set; }

    /// <summary>
    /// Optional Organization ID, sent as <c>X-Org-Id</c>. Used by
    /// admin-tier callers operating across tenants.
    /// </summary>
    public string? OrganizationId { get; set; }

    /// <summary>
    /// This agent's OWN id (ADR-007 §4.1). NOT sent on the gateway call (that FORWARDS the
    /// captured inbound caller); used by <see cref="ShashuClient.BuildInvocationHeaders"/>
    /// to stamp <c>caller = self</c> when THIS agent invokes a downstream agent. Seeds the
    /// self-id cache, which is then refreshed from each gateway response's
    /// <c>x-shashu-agent-id</c> header. Advisory lineage only — NEVER an auth credential.
    /// </summary>
    public string? AgentId { get; set; }

    /// <summary>
    /// Default session this client joins. Omitted, a session is minted once
    /// at construction so every request the client makes joins it. A captured
    /// inbound A2A session overrides this default; a per-request
    /// <c>sessionId:</c> is an instruction that wins. See
    /// <c>contracts/sdk-surface.v1.md</c> "Which session a request joins".
    /// </summary>
    public string? SessionId { get; set; }

    /// <summary>
    /// The contract version this SDK declares on every request it sends:
    /// <c>2026-09-01</c>, the value the Python SDK sends.
    /// </summary>
    public const string DefaultContractVersion = "2026-09-01";

    /// <summary>
    /// Sent as the <c>shashu-version</c> request header. Defaults to
    /// <see cref="DefaultContractVersion"/>. Set another date to pin it, or
    /// <c>null</c> to send no such header and take whatever the gateway's
    /// account default is (on a stock gateway that is the legacy contract:
    /// a refusal arrives as a 200 reply and the body carries no decision).
    /// </summary>
    /// <remarks>
    /// Without this header the gateway serves its legacy contract, where a
    /// refusal is an HTTP 200 and memory and inspection details reach only the
    /// <c>x-shashu-*</c> count headers. With it, a refusal is an error
    /// (400 for a content block, 403 for a policy refusal) and the reply body
    /// carries the full decision, which <see cref="Shashu.DecisionOf"/> reads.
    /// A <c>shashu-version</c> already on a caller-supplied
    /// <see cref="HttpClient"/>'s <c>DefaultRequestHeaders</c> is left alone.
    /// </remarks>
    public string? ContractVersion { get; set; } = DefaultContractVersion;

    /// <summary>
    /// Default user-agent. Override only for testing or for proxy/CDN
    /// debugging. The default identifies the SDK + version for telemetry.
    /// </summary>
    public string UserAgent { get; set; } = "shashu-dotnet/1.3.0";
}
