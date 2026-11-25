using System.Net;
using System.Net.Http.Json;
using Shashu;
using Shashu.Routing;
using Xunit;

namespace Shashu.Tests;

/// <summary>
/// Hermetic tests for gateway routing. Stubs the inner HTTP handler so
/// we capture exactly what URL + headers the handler emits given a
/// vendor-shaped request — no live OpenAI / Anthropic / Google calls.
/// </summary>
public class ShashuRoutingHandlerTests
{
    private static (ShashuRoutingHandler handler, CapturingInnerHandler captured) BuildHandler(
        string gatewayUrl = "https://gateway.test.invalid/")
    {
        var captured = new CapturingInnerHandler();
        var opts = new ShashuRoutingOptions { BaseUrl = new Uri(gatewayUrl) };
        var handler = new ShashuRoutingHandler(
            apiKey: "sk-trustgate-fake",
            innerHandler: captured,
            options: opts);
        return (handler, captured);
    }

    [Fact]
    public async Task OpenAI_request_is_rerouted_to_gateway_with_Shashu_bearer()
    {
        var (handler, captured) = BuildHandler();
        var http = new HttpClient(handler);

        // Customer code (unchanged): construct a request as if calling OpenAI directly.
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "sk-real-openai-key");
        req.Content = JsonContent.Create(new { model = "gpt-4o", messages = new[] { new { role = "user", content = "hi" } } });

        await http.SendAsync(req);

        // What the inner handler saw — i.e. what actually went on the wire.
        var seen = captured.LastRequest!;
        Assert.Equal("gateway.test.invalid", seen.RequestUri!.Host);
        Assert.Equal("/v1/chat/completions", seen.RequestUri.AbsolutePath);
        Assert.Equal("Bearer sk-trustgate-fake", seen.Headers.Authorization!.ToString());
        // Vendor key dropped:
        Assert.False(seen.Headers.Authorization.Parameter == "sk-real-openai-key");
        Assert.True(seen.Headers.Contains("x-shashu-trace-id"));
        Assert.True(seen.Headers.Contains("x-shashu-session-id"));
    }

    [Fact]
    public async Task Busy_retry_resends_the_same_body_and_shashu_headers()
    {
        var inner = new SequenceInnerHandler();
        var handler = new ShashuRoutingHandler(
            "sk-trustgate-fake",
            inner,
            new ShashuRoutingOptions { BaseUrl = new Uri("https://gateway.test.invalid/") });
        var http = new HttpClient(handler);
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions");
        req.Content = JsonContent.Create(new { model = "gpt-4o", messages = new[] { new { role = "user", content = "hi" } } });
        await http.SendAsync(req);

        Assert.Equal(2, inner.Bodies.Count);
        Assert.Equal(inner.Bodies[0], inner.Bodies[1]);
        Assert.Equal(inner.Headers[0]["x-shashu-trace-id"], inner.Headers[1]["x-shashu-trace-id"]);
        Assert.Equal(inner.Headers[0]["x-shashu-session-id"], inner.Headers[1]["x-shashu-session-id"]);
        Assert.False(string.IsNullOrWhiteSpace(inner.Headers[0]["x-shashu-trace-id"]));
        Assert.False(string.IsNullOrWhiteSpace(inner.Headers[0]["x-shashu-session-id"]));
    }

    [Fact]
    public async Task Anthropic_request_is_rerouted_with_xapikey_dropped()
    {
        var (handler, captured) = BuildHandler();
        var http = new HttpClient(handler);

        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages");
        req.Headers.Add("x-api-key", "sk-ant-fake");
        req.Headers.Add("anthropic-version", "2023-06-01");
        req.Content = JsonContent.Create(new { model = "claude-sonnet-4-6", messages = new[] { new { role = "user", content = "hi" } } });

        await http.SendAsync(req);

        var seen = captured.LastRequest!;
        Assert.Equal("gateway.test.invalid", seen.RequestUri!.Host);
        Assert.Equal("/v1/messages", seen.RequestUri.AbsolutePath);
        Assert.Equal("Bearer sk-trustgate-fake", seen.Headers.Authorization!.ToString());
        // Vendor-specific auth headers stripped — gateway never forwards them.
        Assert.False(seen.Headers.Contains("x-api-key"));
        Assert.False(seen.Headers.Contains("anthropic-version"));
    }

    [Fact]
    public async Task Google_request_is_remapped_to_OpenAI_compatible_path()
    {
        var (handler, captured) = BuildHandler();
        var http = new HttpClient(handler);

        using var req = new HttpRequestMessage(
            HttpMethod.Post,
            "https://generativelanguage.googleapis.com/v1beta/models/gemini-pro:generateContent");
        req.Headers.Add("x-goog-api-key", "AIza-fake");
        req.Content = JsonContent.Create(new { contents = new[] { new { parts = new[] { new { text = "hi" } } } } });

        await http.SendAsync(req);

        var seen = captured.LastRequest!;
        Assert.Equal("gateway.test.invalid", seen.RequestUri!.Host);
        // Google's vendor path is mapped to the gateway's OpenAI-compatible endpoint;
        // gateway has a Google-shape adapter that reshapes the body server-side.
        Assert.Equal("/v1/chat/completions", seen.RequestUri.AbsolutePath);
        Assert.Equal("Bearer sk-trustgate-fake", seen.Headers.Authorization!.ToString());
        Assert.False(seen.Headers.Contains("x-goog-api-key"));
    }

    [Fact]
    public async Task Non_provider_host_passes_through_untouched()
    {
        var (handler, captured) = BuildHandler();
        var http = new HttpClient(handler);

        // Customer's own API call — must not be intercepted.
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://internal.acme.com/users/me");
        req.Headers.Add("X-Custom-Auth", "secret-token-123");

        await http.SendAsync(req);

        var seen = captured.LastRequest!;
        Assert.Equal("internal.acme.com", seen.RequestUri!.Host);
        Assert.Equal("/users/me", seen.RequestUri.AbsolutePath);
        Assert.Null(seen.Headers.Authorization);
        Assert.Equal("secret-token-123", seen.Headers.GetValues("X-Custom-Auth").Single());
        Assert.False(seen.Headers.Contains("x-shashu-trace-id"));
        Assert.False(seen.Headers.Contains("x-shashu-session-id"));
    }

    [Fact]
    public async Task Query_string_is_preserved_through_rewrite()
    {
        var (handler, captured) = BuildHandler();
        var http = new HttpClient(handler);

        using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.openai.com/v1/models?limit=10&order=desc");
        await http.SendAsync(req);

        var seen = captured.LastRequest!;
        Assert.Equal("gateway.test.invalid", seen.RequestUri!.Host);
        Assert.Equal("/v1/models", seen.RequestUri.AbsolutePath);
        Assert.Equal("?limit=10&order=desc", seen.RequestUri.Query);
    }

    [Fact]
    public async Task UserAgent_is_appended_for_telemetry_attribution()
    {
        var (handler, captured) = BuildHandler();
        var http = new HttpClient(handler);

        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions");
        // Vendor SDK already set its own UA — we must append, not replace.
        req.Headers.UserAgent.ParseAdd("OpenAI/1.0");

        await http.SendAsync(req);

        var seen = captured.LastRequest!;
        var ua = seen.Headers.UserAgent.ToString();
        Assert.Contains("OpenAI/1.0", ua);
        Assert.Contains("shashu-dotnet/", ua);
    }

    [Fact]
    public void Provider_host_check_is_case_insensitive()
    {
        Assert.True(ShashuRoutingHandler.IsProviderHost(new Uri("https://API.OpenAI.COM/v1/chat/completions")));
        Assert.True(ShashuRoutingHandler.IsProviderHost(new Uri("https://api.anthropic.com/v1/messages")));
        Assert.False(ShashuRoutingHandler.IsProviderHost(new Uri("https://example.com/")));
    }

    [Fact]
#pragma warning disable CS0618 // deprecated aliases are still public API — hold them to it
    public async Task Deprecated_Patching_aliases_still_route_to_the_gateway()
    {
        var captured = new CapturingInnerHandler();
        var opts = new global::Shashu.Patching.ShashuRoutingOptions
        {
            BaseUrl = new Uri("https://gateway.test.invalid/"),
        };
        var handler = global::Shashu.Patching.ProviderPatch.AsHttpHandler(
            apiKey: "sk-trustgate-fake", options: opts, innerHandler: captured);
        var http = new HttpClient(handler);

        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions");
        await http.SendAsync(req);

        Assert.Equal("https://gateway.test.invalid/v1/chat/completions",
            captured.LastRequest!.RequestUri!.ToString());
        Assert.Equal("Bearer sk-trustgate-fake",
            captured.LastRequest!.Headers.Authorization!.ToString());

        // The alias handler is the routing handler, not a look-alike.
        var direct = new global::Shashu.Patching.ShashuRoutingHandler(
            "sk-trustgate-fake", new CapturingInnerHandler(), opts);
        Assert.IsAssignableFrom<ShashuRoutingHandler>(direct);
        Assert.IsAssignableFrom<ShashuRoutingOptions>(opts);
    }
#pragma warning restore CS0618

    [Fact]
    public void Shashu_facade_throws_when_api_key_missing()
    {
        Environment.SetEnvironmentVariable("SHASHU_API_KEY", null);
        Environment.SetEnvironmentVariable("SHASHU_API_KEY", null);
        Assert.Throws<ShashuConfigException>(() => GatewayRouting.AsHttpHandler());
    }

    [Fact]
    public async Task Shashu_facade_picks_up_env_var_api_key()
    {
        Environment.SetEnvironmentVariable("SHASHU_API_KEY", "sk-trustgate-env-key");
        try
        {
            var captured = new CapturingInnerHandler();
            var handler = GatewayRouting.AsHttpHandler(
                options: new ShashuRoutingOptions { BaseUrl = new Uri("https://gateway.test.invalid/") },
                innerHandler: captured);
            var http = new HttpClient(handler);

            using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions");
            await http.SendAsync(req);

            Assert.Equal("Bearer sk-trustgate-env-key", captured.LastRequest!.Headers.Authorization!.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("SHASHU_API_KEY", null);
        }
    }
}

/// <summary>Stub HttpMessageHandler that records the last request and returns 200 OK.</summary>
internal sealed class CapturingInnerHandler : HttpMessageHandler
{
    public HttpRequestMessage? LastRequest { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Clone the request URI + headers — the request is disposed after
        // the call returns, so we can't keep a live reference to .Headers.
        // For simplicity we keep the reference; tests inspect it before
        // returning. (HttpRequestMessage isn't Dispose'd automatically by
        // SendAsync.)
        LastRequest = request;
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }
}

/// <summary>503 with Retry-After=0, then 200. Copies body and headers off each send.</summary>
internal sealed class SequenceInnerHandler : HttpMessageHandler
{
    public List<byte[]> Bodies { get; } = new();
    public List<Dictionary<string, string>> Headers { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null
            ? Array.Empty<byte>()
            : await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        Bodies.Add(body);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in request.Headers)
            headers[h.Key] = string.Join(",", h.Value);
        Headers.Add(headers);

        if (Bodies.Count == 1)
        {
            var busy = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("{\"error\":{\"message\":\"busy\",\"code\":\"gateway_busy\"}}"),
            };
            busy.Headers.TryAddWithoutValidation("Retry-After", "0");
            busy.Headers.TryAddWithoutValidation("x-should-retry", "true");
            return busy;
        }
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"choices\":[]}"),
        };
    }
}
