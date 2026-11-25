using System.Net.Http.Headers;
using Shashu.Configuration;
using Shashu.Internal;

namespace Shashu.Routing;

/// <summary>
/// HTTP middleware that intercepts requests destined for known LLM
/// provider hosts (OpenAI, Anthropic, Google) and reroutes them through
/// the Shashu gateway. The customer's existing vendor-SDK code stays
/// unchanged; only the underlying transport changes.
/// </summary>
/// <remarks>
/// <para><strong>Nothing is rewired at run time.</strong> This SDK does not
/// modify, replace or re-enter another vendor's library. You construct an
/// <c>HttpClient</c> over this handler and hand that client to the vendor
/// SDK at construction; every call site downstream of that constructor is
/// unchanged, and the routing is visible in the code that built the client
/// rather than installed behind it.</para>
///
/// <para>What gets rewritten on intercepted requests:
///   <list type="bullet">
///     <item>Host: rewritten to the configured gateway base URL.</item>
///     <item>Path: provider-specific paths are mapped to the gateway's
///       OpenAI-compatible <c>/v1/chat/completions</c> equivalent.</item>
///     <item><c>Authorization</c>: replaced with the Shashu Bearer
///       token (the original vendor key is dropped — the gateway holds
///       upstream provider keys server-side, never trusts client-supplied
///       upstream auth).</item>
///   </list>
/// </para>
///
/// <para>Usage — wire the handler into any HttpClient:
///   <code>
///   var handler = new ShashuRoutingHandler("tg_sk_...", new HttpClientHandler());
///   var http    = new HttpClient(handler) { BaseAddress = new Uri("https://api.openai.com") };
///   // Any vendor SDK that lets you inject HttpClient now routes through Shashu.
///   </code>
/// </para>
/// </remarks>
public class ShashuRoutingHandler : DelegatingHandler
{
    private readonly Uri _gatewayBaseUrl;
    private readonly CredentialResolver _resolver;
    private readonly ShashuRoutingOptions _options;

    /// <summary>
    /// Hosts the handler recognises as upstream LLM providers and reroutes
    /// to the gateway. Keep in sync with the Java interceptor's list.
    /// </summary>
    private static readonly HashSet<string> _providerHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "api.openai.com",
        "api.anthropic.com",
        "generativelanguage.googleapis.com",
        // Future: "api.cohere.ai", "api.mistral.ai", "api.together.xyz" (extend
        // when those vendors land in customer requests).
    };

    /// <summary>
    /// Build a handler that rewrites known provider hosts to the SHASHU gateway.
    /// </summary>
    public ShashuRoutingHandler(
        string? apiKey,
        HttpMessageHandler? innerHandler = null,
        ShashuRoutingOptions? options = null)
    {
        _options = options ?? new ShashuRoutingOptions();
        _resolver = new CredentialResolver(
            apiKey: string.IsNullOrWhiteSpace(apiKey) ? null : apiKey,
            tokenProvider: _options.TokenProvider,
            requireOne: true);
        _gatewayBaseUrl = ShashuEnv.RequireBaseUrl(
            _options.BaseUrl,
            entryPoint: "new ShashuRoutingHandler()",
            argument: "ShashuRoutingOptions.BaseUrl");

        InnerHandler = innerHandler ?? new HttpClientHandler();
    }

    /// <summary>True if this URL points at a known upstream provider host.</summary>
    public static bool IsProviderHost(Uri uri) =>
        uri.Host is { } h && _providerHosts.Contains(h);

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri is null || !IsProviderHost(request.RequestUri))
        {
            // Not a known LLM provider — pass through unchanged. Customers
            // using ShashuRoutingHandler as a top-level handler keep
            // ordinary HTTP traffic working. No retrieval declaration is
            // attached either: it is declared for the MODEL call, and a
            // request that is not going to the gateway must not carry a
            // customer's document previews to some third party.
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        // Rewrite to gateway. We preserve the original path + query so the
        // gateway's OpenAI-compatible router can match (`/v1/chat/completions`
        // etc.). Provider-specific paths are mapped in MapProviderPath below.
        request.RequestUri = MapToGatewayUri(request.RequestUri);

        var token = await _resolver.GetAsync(cancellationToken).ConfigureAwait(false);
        request.Headers.Authorization = string.IsNullOrEmpty(token)
            ? null
            : new AuthenticationHeaderValue("Bearer", token);

        // Vendor-specific auth headers (Anthropic's `x-api-key`, Google's
        // `x-goog-api-key`) get stripped — the gateway treats them as no-ops
        // anyway, and stripping prevents the upstream provider from seeing
        // them if the gateway's egress proxy ever forwarded a request.
        request.Headers.Remove("x-api-key");
        request.Headers.Remove("x-goog-api-key");
        request.Headers.Remove("anthropic-version");

        // User-Agent annotation — telemetry attribution. Doesn't replace the
        // vendor SDK's UA; appends to it.
        if (!request.Headers.UserAgent.Any(p => p.Product?.Name == "shashu-dotnet"))
        {
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("shashu-dotnet", ShashuClient.SdkVersion));
        }

        // Optional org scoping — admin-tier callers operating across tenants.
        if (!string.IsNullOrWhiteSpace(_options.OrganizationId)
            && !request.Headers.Contains("X-Org-Id"))
        {
            request.Headers.Add("X-Org-Id", _options.OrganizationId);
        }

        // Declare the contract version, as the Python SDK's patched clients do,
        // so a refusal reaches the vendor SDK as its own error rather than as
        // a 200 reply. A value the caller set on the request wins.
        if (!string.IsNullOrWhiteSpace(_options.ContractVersion)
            && !request.Headers.Contains(ShashuApi.ContractVersionHeader))
        {
            request.Headers.TryAddWithoutValidation(ShashuApi.ContractVersionHeader, _options.ContractVersion);
        }

        // Ambient retrieval, joined last so the body it rewrites is final. The
        // first thing this does on an empty bus is return, without reading or
        // re-serialising the body.
        await RetrievalJoiner.JoinAsync(request, cancellationToken).ConfigureAwait(false);

        if (_resolver.UsesProvider && request.Content is not null)
        {
            var bytes = await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            var type = request.Content.Headers.ContentType;
            request.Content = new ByteArrayContent(bytes);
            if (type is not null)
                request.Content.Headers.ContentType = type;
        }

        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!_resolver.UsesProvider || (int)response.StatusCode != 401)
            return response;

        var raw = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var status = (int)response.StatusCode;
        var code = CredentialResolver.PassRefusalCode(status, raw);
        if (!CredentialResolver.ShouldRetryPassRefused(
                usesProvider: true, status, code, bytesDelivered: false))
        {
            response.Content = new StringContent(raw, System.Text.Encoding.UTF8, "application/json");
            return response;
        }

        response.Dispose();
        _resolver.Invalidate();
        var retryToken = await _resolver.GetAsync(cancellationToken).ConfigureAwait(false);
        request.Headers.Authorization = string.IsNullOrEmpty(retryToken)
            ? null
            : new AuthenticationHeaderValue("Bearer", retryToken);
        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private Uri MapToGatewayUri(Uri originalUri)
    {
        var gatewayPath = MapProviderPath(originalUri.Host, originalUri.AbsolutePath);
        var builder = new UriBuilder(_gatewayBaseUrl)
        {
            Path = gatewayPath,
            Query = originalUri.Query.TrimStart('?'),
        };
        return builder.Uri;
    }

    /// <summary>
    /// Maps a vendor-specific request path to the gateway's
    /// OpenAI-compatible equivalent. The gateway handles the provider mux
    /// internally based on the <c>model</c> field in the request body, so
    /// we just need to land on the right OpenAI-compat endpoint.
    /// </summary>
    /// <remarks>
    /// Known mappings (extend as new vendors come on):
    /// <list type="bullet">
    ///   <item><c>api.openai.com/v1/chat/completions</c> → <c>/v1/chat/completions</c> (passthrough)</item>
    ///   <item><c>api.openai.com/v1/completions</c> → <c>/v1/completions</c></item>
    ///   <item><c>api.openai.com/v1/responses</c> → <c>/v1/responses</c></item>
    ///   <item><c>api.anthropic.com/v1/messages</c> → <c>/v1/messages</c> (gateway accepts Anthropic-shape too)</item>
    ///   <item><c>generativelanguage.googleapis.com/v1beta/models/*:generateContent</c> → <c>/v1/chat/completions</c></item>
    /// </list>
    /// </remarks>
    internal static string MapProviderPath(string host, string originalPath)
    {
        // OpenAI + Anthropic paths are already on the gateway's accepted set.
        if (host.Equals("api.openai.com", StringComparison.OrdinalIgnoreCase)
            || host.Equals("api.anthropic.com", StringComparison.OrdinalIgnoreCase))
        {
            return originalPath;
        }

        // Google generativeai paths look like:
        // /v1beta/models/gemini-pro:generateContent
        // /v1beta/models/gemini-pro:streamGenerateContent
        // The gateway has a Google-shape adapter at /v1/chat/completions; the
        // adapter inspects body.contents[].parts[].text and reshapes.
        if (host.Equals("generativelanguage.googleapis.com", StringComparison.OrdinalIgnoreCase))
        {
            return "/v1/chat/completions";
        }

        // Unknown — let it through unchanged. The gateway will 404 if it
        // doesn't recognise; better than silently mis-routing.
        return originalPath;
    }
}

/// <summary>Optional configuration for the routing handler.</summary>
public class ShashuRoutingOptions
{
    /// <summary>
    /// Your gateway's base URL. Falls back to the <c>SHASHU_BASE_URL</c>
    /// environment variable.
    /// </summary>
    /// <remarks>
    /// There is no default and the SDK will not guess one. When neither this
    /// nor the environment variable resolves, the handler's constructor throws
    /// <see cref="ShashuConfigException"/>.
    /// </remarks>
    public Uri? BaseUrl { get; set; }

    /// <summary>Cloud-pass provider. Give either this or an API key, not both.</summary>
    public Func<CancellationToken, ValueTask<string>>? TokenProvider { get; set; }

    /// <summary>
    /// Optional <c>X-Org-Id</c> header value for admin-tier callers.
    /// </summary>
    public string? OrganizationId { get; set; }

    /// <summary>
    /// Sent as the <c>shashu-version</c> header on every request this
    /// handler reroutes to the gateway. Defaults to
    /// <see cref="ShashuConfig.DefaultContractVersion"/>, so a refusal
    /// reaches the vendor SDK as its own error (400 or 403) rather than as a 200
    /// reply. <c>null</c> sends no such header. A value the caller already set
    /// on the request is left alone.
    /// </summary>
    public string? ContractVersion { get; set; } = ShashuConfig.DefaultContractVersion;
}
