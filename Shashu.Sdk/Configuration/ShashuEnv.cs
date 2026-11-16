namespace Shashu.Configuration;

/// <summary>
/// Gateway address resolution, API key lookup, and the SDK kill switch.
/// SHASHU_* wins; TRUSTGATE_* is a one-time-warned fallback.
/// </summary>
internal static class ShashuEnv
{
    internal const string BaseUrlEnv = "SHASHU_BASE_URL";
    internal const string BaseUrlEnvLegacy = "TRUSTGATE_BASE_URL";
    internal const string ApiKeyEnv = "SHASHU_API_KEY";
    internal const string ApiKeyEnvLegacy = "TRUSTGATE_API_KEY";
    internal const string DisableEnv = "SHASHU_DISABLE";
    internal const string DisableEnvLegacy = "TRUSTGATE_DISABLE";

    private static readonly HashSet<string> Truthy =
        new(StringComparer.OrdinalIgnoreCase) { "1", "true", "yes", "on" };

    private static readonly HashSet<string> FallbackWarned = new(StringComparer.Ordinal);
    private static readonly object WarnLock = new();

    internal static bool IsDisabled()
    {
        var value = EnvGet(DisableEnv, DisableEnvLegacy);
        return value is not null && Truthy.Contains(value);
    }

    internal static string? ApiKey() => EnvGet(ApiKeyEnv, ApiKeyEnvLegacy);

    internal static string? EnvGet(string current, string legacy)
    {
        var value = (Environment.GetEnvironmentVariable(current) ?? string.Empty).Trim();
        if (value.Length > 0)
            return value;
        var old = (Environment.GetEnvironmentVariable(legacy) ?? string.Empty).Trim();
        if (old.Length > 0)
        {
            WarnLegacyOnce(legacy, current);
            return old;
        }
        return null;
    }

    internal static void WarnLegacyOnce(string legacy, string current)
    {
        lock (WarnLock)
        {
            if (!FallbackWarned.Add(legacy))
                return;
        }
        Console.Error.WriteLine(
            $"{legacy} is deprecated; set {current} instead. "
            + "The TRUSTGATE_* name still works for this release.");
    }

    internal static Uri? OptionalBaseUrl(Uri? baseUrl)
    {
        if (baseUrl is not null) return baseUrl;

        var fromEnv = EnvGet(BaseUrlEnv, BaseUrlEnvLegacy);
        if (string.IsNullOrWhiteSpace(fromEnv)) return null;

        return Uri.TryCreate(fromEnv.Trim(), UriKind.Absolute, out var parsed) ? parsed : null;
    }

    internal static Uri RequireBaseUrl(Uri? baseUrl, string entryPoint, string argument)
    {
        var resolved = OptionalBaseUrl(baseUrl);
        if (resolved is not null) return resolved;

        throw new ShashuConfigException(
            $"SHASHU gateway address is not configured, so {entryPoint} has "
            + $"nothing to route to. Pass {argument} to {entryPoint}, or set one of the "
            + $"{BaseUrlEnv} or {BaseUrlEnvLegacy} environment variables. SHASHU is self-hosted: the "
            + "SDK has no default address and will not guess one.");
    }
}
