namespace Shashu.Configuration;

/// <summary>
/// Gateway address resolution, API key lookup, and the SDK kill switch.
/// From 1.1.0 the SDK reads only SHASHU_* names. The old environment-variable names are no longer read.
/// </summary>
internal static class ShashuEnv
{
    internal const string BaseUrlEnv = "SHASHU_BASE_URL";
    internal const string ApiKeyEnv = "SHASHU_API_KEY";
    internal const string DisableEnv = "SHASHU_DISABLE";

    private static readonly HashSet<string> Truthy =
        new(StringComparer.OrdinalIgnoreCase) { "1", "true", "yes", "on" };

    internal static bool IsDisabled()
    {
        var value = EnvGet(DisableEnv);
        return value is not null && Truthy.Contains(value);
    }

    internal static string? ApiKey() => EnvGet(ApiKeyEnv);

    internal static string? EnvGet(string current, string? unusedLegacy = null)
    {
        var value = (Environment.GetEnvironmentVariable(current) ?? string.Empty).Trim();
        return value.Length > 0 ? value : null;
    }

    internal static Uri? OptionalBaseUrl(Uri? baseUrl)
    {
        if (baseUrl is not null) return baseUrl;

        var fromEnv = EnvGet(BaseUrlEnv);
        if (string.IsNullOrWhiteSpace(fromEnv)) return null;

        return Uri.TryCreate(fromEnv.Trim(), UriKind.Absolute, out var parsed) ? parsed : null;
    }

    internal static Uri RequireBaseUrl(Uri? baseUrl, string entryPoint, string argument)
    {
        var resolved = OptionalBaseUrl(baseUrl);
        if (resolved is not null) return resolved;

        throw new ShashuConfigException(
            $"SHASHU gateway address is not configured, so {entryPoint} has "
            + $"nothing to route to. Pass {argument} to {entryPoint}, or set the "
            + $"{BaseUrlEnv} environment variable. SHASHU is self-hosted: the "
            + "SDK has no default address and will not guess one.");
    }
}
