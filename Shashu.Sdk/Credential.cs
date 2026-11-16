using System.Text.Json;

namespace Shashu;

/// <summary>
/// SHASHU key vs cloud-pass credentials.
/// </summary>
/// <remarks>
/// The SDK never talks to Entra, Google or AWS. A
/// <see cref="ShashuConfig.TokenProvider"/> returns the current pass; this
/// type caches it, refreshes it 60 seconds before <c>exp</c>, and retries a
/// request ONCE — after fetching a new pass — when the gateway refuses the
/// pass as expired.
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design",
    "CA1001",
    Justification = "SemaphoreSlim is released after every wait; the owning client/handler outlives the resolver.")]
internal sealed class CredentialResolver
{
    /// <summary>Give either a SHASHU key or a token provider, not both.</summary>
    public const string BothOrNeither = "Give either a SHASHU key or a token provider, not both.";

    public const int RefreshSkewSeconds = 60;

    public const string PASS_PROVIDER_NOT_SET_UP = "pass_provider_not_set_up";
    public const string PASS_NOT_LICENSED = "pass_not_licensed";
    public const string PASS_EXPIRED = "pass_expired";
    public const string PASS_WRONG_AUDIENCE = "pass_wrong_audience";
    public const string PASS_WRONG_ISSUER = "pass_wrong_issuer";
    public const string PASS_UNKNOWN_AGENT = "pass_unknown_agent";

    public static readonly string[] PASS_CODES =
    [
        PASS_PROVIDER_NOT_SET_UP,
        PASS_NOT_LICENSED,
        PASS_EXPIRED,
        PASS_WRONG_AUDIENCE,
        PASS_WRONG_ISSUER,
        PASS_UNKNOWN_AGENT,
    ];

    private static readonly HashSet<string> PassCodeSet = new(PASS_CODES, StringComparer.Ordinal);

    /// <summary>Injected clock for tests. Unix seconds.</summary>
    internal static Func<long> UnixNow { get; set; } = static () => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private readonly Func<CancellationToken, ValueTask<string>>? _provider;
    private readonly string? _static;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _cached;

    public CredentialResolver(
        string? apiKey = null,
        Func<CancellationToken, ValueTask<string>>? tokenProvider = null,
        bool requireOne = false)
    {
        var envKey = global::Shashu.Configuration.ShashuEnv.ApiKey();
        var hasKeyArg = apiKey is not null;
        var hasProvider = tokenProvider is not null;
        if (hasKeyArg && hasProvider)
            throw new ShashuConfigException(BothOrNeither);
        if (hasProvider && envKey is not null && !hasKeyArg)
            throw new ShashuConfigException(BothOrNeither);
        if (requireOne && !hasProvider && !hasKeyArg && envKey is null)
            throw new ShashuConfigException(BothOrNeither);

        _provider = tokenProvider;
        _static = hasProvider ? null : (hasKeyArg ? apiKey : envKey);
    }

    public bool UsesProvider => _provider is not null;

    public async ValueTask<string?> GetAsync(CancellationToken cancellationToken = default)
    {
        if (_provider is null)
            return _static;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cached is not null && !NeedsRefresh(_cached))
                return _cached;
            var token = await _provider(cancellationToken).ConfigureAwait(false);
            _cached = token;
            return _cached;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Invalidate()
    {
        _gate.Wait();
        try { _cached = null; }
        finally { _gate.Release(); }
    }

    internal static bool NeedsRefresh(string token)
    {
        var exp = JwtExpUnverified(token);
        if (exp is null)
            return true;
        return UnixNow() >= (exp.Value - RefreshSkewSeconds);
    }

    public static long? JwtExpUnverified(string token)
    {
        var parts = token.Split('.');
        if (parts.Length != 3)
            return null;
        var payload = parts[1];
        var pad = (4 - (payload.Length % 4)) % 4;
        try
        {
            var bytes = Convert.FromBase64String(
                payload.Replace('-', '+').Replace('_', '/') + new string('=', pad));
            using var doc = JsonDocument.Parse(bytes);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("exp", out var expEl))
                return null;
            if (expEl.ValueKind == JsonValueKind.Number && expEl.TryGetInt64(out var n))
                return n;
            if (expEl.ValueKind == JsonValueKind.String
                && long.TryParse(expEl.GetString(), out var parsed))
                return parsed;
            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static string? PassRefusalCode(int statusCode, string body)
    {
        if (statusCode != 401)
            return null;
        var code = CodeFromBody(body);
        return code is not null && PassCodeSet.Contains(code) ? code : null;
    }

    public static string? CodeFromBody(string body)
    {
        if (string.IsNullOrEmpty(body))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("error", out var err)
                && err.ValueKind == JsonValueKind.Object
                && err.TryGetProperty("code", out var codeEl)
                && codeEl.ValueKind == JsonValueKind.String)
            {
                return codeEl.GetString();
            }
        }
        catch (JsonException)
        {
        }
        return null;
    }

    public static string? MessageFromBody(string body)
    {
        if (string.IsNullOrEmpty(body))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("error", out var err)
                && err.ValueKind == JsonValueKind.Object
                && err.TryGetProperty("message", out var msgEl)
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

    public static bool ShouldRetryPassRefused(
        bool usesProvider, int statusCode, string? code, bool bytesDelivered)
    {
        if (!usesProvider || bytesDelivered || statusCode != 401)
            return false;
        return code == PASS_EXPIRED;
    }
}
