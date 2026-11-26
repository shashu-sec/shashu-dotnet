namespace Shashu.Routing;

/// <summary>
/// Static factory for an <see cref="HttpClient"/> — or a bare
/// <see cref="HttpMessageHandler"/> — that routes provider traffic through
/// your Shashu gateway.
/// </summary>
/// <remarks>
/// <para><strong>Nothing is rewired at run time.</strong> This SDK does not
/// modify, replace or re-enter another vendor's library. It builds an
/// <c>HttpClient</c> whose transport points at your gateway, and you pass
/// that client to the vendor SDK at construction. Every call site downstream
/// of that constructor is unchanged, and the routing is visible in the code
/// that built the client.</para>
///
/// <code>
/// using Shashu.Routing;
///
/// // Once at app startup:
/// var handler = GatewayRouting.AsHttpHandler("sk-shashu-...");
/// var http    = new HttpClient(handler);
///
/// // Pass to whatever vendor SDK accepts a custom HttpClient:
/// var openai    = new OpenAIClient(httpClient: http, ...);
/// var anthropic = new AnthropicClient(httpClient: http, ...);
/// </code>
///
/// <para>API keys can be passed explicitly or read from the
/// <c>SHASHU_API_KEY</c> environment variable.</para>
///
/// <para>Note: this class lives in the <c>Shashu.Routing</c> namespace
/// (not the root <c>Shashu</c>) to avoid a name collision with the
/// namespace itself.</para>
/// </remarks>
public static class GatewayRouting
{
    /// <summary>
    /// Build a <see cref="HttpMessageHandler"/> that routes provider
    /// traffic (api.openai.com / api.anthropic.com / generativelanguage.googleapis.com)
    /// through the Shashu gateway. Pass to any vendor SDK that accepts a
    /// custom <c>HttpClient</c>.
    /// </summary>
    /// <param name="apiKey">Shashu API key (sk-shashu-...). Falls back to <c>SHASHU_API_KEY</c> env var.</param>
    /// <param name="options">Optional gateway URL + org-id override.</param>
    /// <param name="innerHandler">Optional inner HTTP handler. Defaults to <c>HttpClientHandler</c>.</param>
    public static HttpMessageHandler AsHttpHandler(
        string? apiKey = null,
        ShashuRoutingOptions? options = null,
        HttpMessageHandler? innerHandler = null) =>
        new ShashuRoutingHandler(apiKey, innerHandler, options);

    /// <summary>
    /// Build an <see cref="HttpClient"/> pre-wired to route provider
    /// traffic through Shashu. Equivalent to wrapping
    /// <see cref="AsHttpHandler"/> in a <c>new HttpClient(handler)</c>.
    /// </summary>
    public static HttpClient NewHttpClient(
        string? apiKey = null,
        ShashuRoutingOptions? options = null) =>
        new(AsHttpHandler(apiKey, options));
}
