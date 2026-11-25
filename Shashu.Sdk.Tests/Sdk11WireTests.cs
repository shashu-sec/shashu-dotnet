using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Shashu;
using Shashu.Chat;
using Shashu.Tests.Harness;
using Xunit;

namespace Shashu.Tests;

public class Sdk11WireTests
{
    private const string BusySentence = "The gateway is busy. Retry after the advertised wait.";

    [Fact]
    public async Task A_Only_shashu_names_are_sent()
    {
        HttpRequestMessage? seen = null;
        var stub = new StubHandler((req, _) =>
        {
            seen = req;
            return Task.FromResult(Ok());
        });
        using var http = new HttpClient(stub) { BaseAddress = new Uri("https://test.invalid/") };
        using var client = new ShashuClient(
            new ShashuConfig { BaseUrl = TestGateway.Uri, ApiKey = "tg_sk_test", MaxRetries = 0 }, http);
        await client.Chat.Completions.CreateAsync(Req());
        Assert.NotNull(seen);
        Assert.DoesNotContain(seen!.Headers, h =>
            h.Key.StartsWith("x-trustgate-", StringComparison.OrdinalIgnoreCase)
            || h.Key.Equals("TrustGate-Version", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(seen.Headers, h =>
            h.Key.Equals("x-shashu-trace-id", StringComparison.OrdinalIgnoreCase)
            || h.Key.Equals("x-shashu-session-id", StringComparison.OrdinalIgnoreCase));
        Assert.True(seen.Headers.TryGetValues("Shashu-Version", out var versions));
        Assert.Contains("2026-09-01", versions);
    }

    [Fact]
    public async Task B_Shashu_response_headers_and_body_are_read()
    {
        var stub = new StubHandler((_, _) =>
        {
            var r = Ok();
            r.Headers.TryAddWithoutValidation("x-shashu-decision", "allow");
            r.Headers.TryAddWithoutValidation("x-shashu-code", "ok");
            r.Headers.TryAddWithoutValidation("Shashu-Version", "2026-09-01");
            r.Content = JsonContent.Create(new
            {
                id = "chatcmpl-mock",
                choices = new[] { new { index = 0, message = new { role = "assistant", content = "ok" }, finish_reason = "stop" } },
                shashu = new { decision = "allow", code = "ok" },
            });
            return Task.FromResult(r);
        });
        using var http = new HttpClient(stub) { BaseAddress = new Uri("https://test.invalid/") };
        using var client = new ShashuClient(
            new ShashuConfig { BaseUrl = TestGateway.Uri, ApiKey = "k", MaxRetries = 0 }, http);
        var outResp = await client.Chat.Completions.CreateAsync(Req());
        var d = Shashu.DecisionOf(outResp);
        Assert.NotNull(d);
        Assert.Equal("allow", d!.Decision);
    }

    [Fact]
    public async Task C_Old_response_names_are_ignored()
    {
        var stub = new StubHandler((_, _) =>
        {
            var r = Ok();
            r.Headers.TryAddWithoutValidation("x-trustgate-decision", "block");
            r.Headers.TryAddWithoutValidation("TrustGate-Version", "2026-09-01");
            r.Content = JsonContent.Create(new
            {
                id = "chatcmpl-mock",
                choices = new[] { new { index = 0, message = new { role = "assistant", content = "ok" }, finish_reason = "stop" } },
                trustgate = new { decision = "block", code = "jailbreak" },
            });
            return Task.FromResult(r);
        });
        using var http = new HttpClient(stub) { BaseAddress = new Uri("https://test.invalid/") };
        using var client = new ShashuClient(
            new ShashuConfig { BaseUrl = TestGateway.Uri, ApiKey = "k", MaxRetries = 0 }, http);
        var outResp = await client.Chat.Completions.CreateAsync(Req());
        Assert.Null(Shashu.DecisionOf(outResp));
    }

    [Fact]
    public async Task D_Shashu_metadata_is_sent_for_rag()
    {
        string? raw = null;
        var stub = new StubHandler(async (req, _) =>
        {
            raw = await req.Content!.ReadAsStringAsync();
            return Ok();
        });
        using var http = new HttpClient(stub) { BaseAddress = new Uri("https://test.invalid/") };
        using var client = new ShashuClient(
            new ShashuConfig { BaseUrl = TestGateway.Uri, ApiKey = "tg_sk_test", MaxRetries = 0 }, http);
        await client.Chat.Completions.CreateAsync(new ChatCompletionRequest
        {
            Model = "m",
            Messages = new[] { new ChatMessage("user", "hi") },
            RagContext = new RagContext
            {
                Corpus = "handbook",
                Docs = new[] { new RagDoc("D", 1, "p") },
            },
        });
        using var doc = JsonDocument.Parse(raw!);
        Assert.Equal("handbook", doc.RootElement.GetProperty("shashu_metadata").GetProperty("rag_context").GetProperty("corpus").GetString());
        Assert.False(doc.RootElement.TryGetProperty("trustgate_metadata", out _));
    }

    [Fact]
    public async Task E_Tg_sk_and_token_provider_error_text()
    {
        Assert.Equal("Give either a SHASHU key or a token provider, not both.", CredentialResolver.BothOrNeither);
        var ex = Assert.Throws<ShashuConfigException>(() =>
            new CredentialResolver("tg_sk_x", _ => ValueTask.FromResult("p"), true));
        Assert.Equal(CredentialResolver.BothOrNeither, ex.Message);
        var r = new CredentialResolver("tg_sk_x", requireOne: true);
        Assert.Equal("tg_sk_x", await r.GetAsync());
    }

    [Fact]
    public async Task Busy_retry_resends_the_same_built_request()
    {
        var bodies = new List<string>();
        var headerBags = new List<string[]>();
        var calls = 0;
        var stub = new StubHandler(async (req, _) =>
        {
            bodies.Add(await req.Content!.ReadAsStringAsync());
            headerBags.Add(
                req.Headers
                    .Select(h => $"{h.Key.ToLowerInvariant()}={string.Join(",", h.Value)}")
                    .OrderBy(x => x, StringComparer.Ordinal)
                    .ToArray());
            var n = Interlocked.Increment(ref calls);
            if (n == 1)
                return Busy();
            return Ok();
        });
        using var http = new HttpClient(stub) { BaseAddress = new Uri("https://test.invalid/") };
        using var client = new ShashuClient(
            new ShashuConfig { BaseUrl = TestGateway.Uri, ApiKey = "k", MaxRetries = 2 }, http);
        await client.Chat.Completions.CreateAsync(Req());
        Assert.Equal(2, bodies.Count);
        Assert.Equal(bodies[0], bodies[1]);
        string[] Strip(string[] rows) => rows.Where(r => !r.StartsWith("authorization=", StringComparison.Ordinal)).ToArray();
        Assert.Equal(Strip(headerBags[0]), Strip(headerBags[1]));
    }

    [Fact]
    public async Task F_Busy_retry_then_succeeds()
    {
        var calls = 0;
        var stub = new StubHandler((_, _) =>
        {
            var n = Interlocked.Increment(ref calls);
            if (n == 1)
                return Task.FromResult(Busy());
            return Task.FromResult(Ok());
        });
        using var http = new HttpClient(stub) { BaseAddress = new Uri("https://test.invalid/") };
        using var client = new ShashuClient(
            new ShashuConfig { BaseUrl = TestGateway.Uri, ApiKey = "k", MaxRetries = 2 }, http);
        await client.Chat.Completions.CreateAsync(Req());
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task F_Busy_gives_up_and_keeps_gateway_sentence()
    {
        var calls = 0;
        var stub = new StubHandler((_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(Busy());
        });
        using var http = new HttpClient(stub) { BaseAddress = new Uri("https://test.invalid/") };
        using var client = new ShashuClient(
            new ShashuConfig { BaseUrl = TestGateway.Uri, ApiKey = "k", MaxRetries = 2 }, http);
        var ex = await Assert.ThrowsAsync<ShashuException>(() => client.Chat.Completions.CreateAsync(Req()));
        Assert.Equal(BusySentence, ex.Message);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task F_No_retry_without_invitation()
    {
        var calls = 0;
        var stub = new StubHandler((_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("{\"error\":{\"message\":\"" + BusySentence + "\",\"code\":\"gateway_busy\",\"shashu\":{\"decision\":\"fail\",\"code\":\"gateway_busy\"}}}"),
            });
        });
        using var http = new HttpClient(stub) { BaseAddress = new Uri("https://test.invalid/") };
        using var client = new ShashuClient(
            new ShashuConfig { BaseUrl = TestGateway.Uri, ApiKey = "k", MaxRetries = 2 }, http);
        await Assert.ThrowsAsync<ShashuException>(() => client.Chat.Completions.CreateAsync(Req()));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task F_MaxRetries_zero_disables()
    {
        var calls = 0;
        var stub = new StubHandler((_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(Busy());
        });
        using var http = new HttpClient(stub) { BaseAddress = new Uri("https://test.invalid/") };
        using var client = new ShashuClient(
            new ShashuConfig { BaseUrl = TestGateway.Uri, ApiKey = "k", MaxRetries = 0 }, http);
        await Assert.ThrowsAsync<ShashuException>(() => client.Chat.Completions.CreateAsync(Req()));
        Assert.Equal(1, calls);
    }

    private static ChatCompletionRequest Req() =>
        new() { Model = "m", Messages = new[] { new ChatMessage("user", "hi") } };

    private static HttpResponseMessage Ok() =>
        new(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new
            {
                id = "chatcmpl-mock",
                choices = new[] { new { index = 0, message = new { role = "assistant", content = "ok" }, finish_reason = "stop" } },
            }),
        };

    private static HttpResponseMessage Busy()
    {
        var r = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("{\"error\":{\"message\":\"" + BusySentence + "\",\"code\":\"gateway_busy\",\"shashu\":{\"decision\":\"fail\",\"code\":\"gateway_busy\"}}}"),
        };
        r.Headers.TryAddWithoutValidation("Retry-After", "0");
        r.Headers.TryAddWithoutValidation("x-should-retry", "true");
        return r;
    }
}
