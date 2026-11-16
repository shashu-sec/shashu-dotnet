using Shashu.Routing;

namespace Shashu.Patching;

/// <summary>
/// Deprecated alias for <see cref="GatewayRouting"/>.
/// </summary>
/// <remarks>
/// <para>The name was wrong: this class never patched anything. It builds an
/// <c>HttpClient</c> that routes provider traffic through your gateway,
/// which you pass to the vendor SDK at construction — no vendor library is
/// modified at run time. Shashu does not ship run-time patching of
/// another company's library in any SDK surface, so the name advertised a
/// technique the code does not use.</para>
/// <para>Every member forwards to <see cref="GatewayRouting"/> and behaviour
/// is unchanged; switch the <c>using</c>. This type will be removed in a
/// future major release.</para>
/// </remarks>
[Obsolete(
    "Renamed to Shashu.Routing.GatewayRouting. Nothing here patches anything: " +
    "it builds an HttpClient that routes through your gateway, which you pass to " +
    "the vendor SDK at construction. Behaviour is unchanged — switch the using.",
    error: false)]
public static class ProviderPatch
{
    /// <summary>
    /// Deprecated. Forwards to <see cref="GatewayRouting.AsHttpHandler"/>.
    /// </summary>
    public static HttpMessageHandler AsHttpHandler(
        string? apiKey = null,
        ShashuRoutingOptions? options = null,
        HttpMessageHandler? innerHandler = null) =>
        GatewayRouting.AsHttpHandler(apiKey, options, innerHandler);

    /// <summary>
    /// Deprecated. Forwards to <see cref="GatewayRouting.NewHttpClient"/>.
    /// </summary>
    public static HttpClient NewHttpClient(
        string? apiKey = null,
        ShashuRoutingOptions? options = null) =>
        GatewayRouting.NewHttpClient(apiKey, options);
}

/// <summary>
/// Deprecated alias for <c>Shashu.Routing.ShashuRoutingHandler</c>.
/// </summary>
/// <remarks>
/// Only the namespace changed. The old namespace was named for run-time
/// patching, which this handler has never done and which Shashu does not
/// ship. This subclass adds nothing and exists so existing code keeps
/// compiling; switch the <c>using</c>. It will be removed in a future major
/// release.
/// </remarks>
[Obsolete(
    "Moved to Shashu.Routing.ShashuRoutingHandler. Only the namespace changed — " +
    "nothing here patches anything. Switch the using.",
    error: false)]
public sealed class ShashuRoutingHandler : global::Shashu.Routing.ShashuRoutingHandler
{
    /// <summary>
    /// Deprecated. Forwards to the
    /// <c>Shashu.Routing.ShashuRoutingHandler</c> constructor.
    /// </summary>
    public ShashuRoutingHandler(
        string apiKey,
        HttpMessageHandler? innerHandler = null,
        ShashuRoutingOptions? options = null)
        : base(apiKey, innerHandler, options)
    {
    }
}

/// <summary>
/// Deprecated alias for <c>Shashu.Routing.ShashuRoutingOptions</c>.
/// </summary>
/// <remarks>
/// Only the namespace changed. Switch the <c>using</c>; this type will be
/// removed in a future major release.
/// </remarks>
[Obsolete(
    "Moved to Shashu.Routing.ShashuRoutingOptions. Only the namespace changed. " +
    "Switch the using.",
    error: false)]
public sealed class ShashuRoutingOptions : global::Shashu.Routing.ShashuRoutingOptions
{
}
