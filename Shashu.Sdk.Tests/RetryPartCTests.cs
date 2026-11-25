using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Shashu;
using Shashu.Chat;
using Shashu.Routing;
using Shashu.Tests.Harness;
using Xunit;

namespace Shashu.Tests;

/// <summary>Part C: busy-retry capture harness (11 scenarios) on the client freeze path.</summary>
public class RetryPartCTests
{
    private const string BusySentence = "The gateway is busy. Retry after the advertised wait.";

    [Fact]
    public async Task C1_503_then_200_byte_identical()
    {
        var cap = new CaptureStub(BusyThenOk);
        await Call(cap, maxRetries: 2);
        Assert.Equal(2, cap.Bodies.Count);
        Assert.Equal(cap.Bodies[0], cap.Bodies[1]);
        AssertSameHeadersExceptAuth(cap);
    }

    [Fact]
    public async Task C2_429_then_200_byte_identical()
    {
        var cap = new CaptureStub(n => n == 1 ? Invited(HttpStatusCode.TooManyRequests) : Ok());
        await Call(cap, maxRetries: 2);
        Assert.Equal(2, cap.Bodies.Count);
        Assert.Equal(cap.Bodies[0], cap.Bodies[1]);
    }

    [Fact]
    public async Task C3_503_without_invitation_does_not_retry()
    {
        var cap = new CaptureStub(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("{\"error\":{\"message\":\"busy\"}}"),
        });
        await Assert.ThrowsAsync<ShashuException>(() => Call(cap, maxRetries: 2));
        Assert.Single(cap.Bodies);
    }

    [Fact]
    public async Task C4_max_retries_zero()
    {
        var cap = new CaptureStub(_ => Busy());
        await Assert.ThrowsAsync<ShashuException>(() => Call(cap, maxRetries: 0));
        Assert.Single(cap.Bodies);
    }

    [Fact]
    public async Task C5_gives_up_after_max()
    {
        var cap = new CaptureStub(_ => Busy());
        await Assert.ThrowsAsync<ShashuException>(() => Call(cap, maxRetries: 2));
        Assert.Equal(3, cap.Bodies.Count);
        Assert.Equal(cap.Bodies[0], cap.Bodies[1]);
        Assert.Equal(cap.Bodies[1], cap.Bodies[2]);
    }

    [Fact]
    public async Task C6_5xx_not_retried_by_default()
    {
        var cap = new CaptureStub(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("{\"error\":{\"message\":\"boom\"}}"),
        });
        await Assert.ThrowsAsync<ShashuException>(() => Call(cap, maxRetries: 2));
        Assert.Single(cap.Bodies);
    }

    [Fact]
    public async Task C7_second_call_does_not_rebuild_from_the_first()
    {
        var cap = new CaptureStub(n => n == 1 ? Busy() : Ok());
        using var http = new HttpClient(cap) { BaseAddress = new Uri("https://test.invalid/") };
        using var client = new ShashuClient(
            new ShashuConfig { BaseUrl = TestGateway.Uri, ApiKey = "k", MaxRetries = 2 }, http);
        await client.Chat.Completions.CreateAsync(Req("first"));
        await client.Chat.Completions.CreateAsync(Req("second"));
        Assert.Equal(3, cap.Bodies.Count);
        Assert.Equal(cap.Bodies[0], cap.Bodies[1]);
        Assert.NotEqual(cap.Bodies[1], cap.Bodies[2]);
        Assert.Contains("first", Encoding.UTF8.GetString(cap.Bodies[0]));
        Assert.Contains("second", Encoding.UTF8.GetString(cap.Bodies[2]));
    }

    [Fact]
    public async Task C8_idempotency_key_none_sent()
    {
        var cap = new CaptureStub(_ => Ok());
        await Call(cap, maxRetries: 0);
        Assert.Single(cap.Bodies);
        Assert.DoesNotContain(cap.Headers[0].Keys, k => k.Contains("idempotency", StringComparison.OrdinalIgnoreCase));
        var json = Encoding.UTF8.GetString(cap.Bodies[0]);
        Assert.DoesNotContain("idempotency", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task C9_authorization_only_overlay()
    {
        var cap = new CaptureStub(BusyThenOk);
        await Call(cap, maxRetries: 2);
        Assert.Equal(cap.Headers[0]["authorization"], cap.Headers[1]["authorization"]);
        AssertSameHeadersExceptAuth(cap);
    }

    [Fact]
    public async Task C10_hop_depth_frozen()
    {
        var cap = new CaptureStub(BusyThenOk);
        await Call(cap, maxRetries: 2);
        cap.Headers[0].TryGetValue("x-shashu-hop-depth", out var a);
        cap.Headers[1].TryGetValue("x-shashu-hop-depth", out var b);
        Assert.Equal(a, b);
    }

    [Fact]
    public async Task Two_concurrent_handler_calls_keep_their_own_declaration()
    {
        var inner = new PerCorpusBusyThenOk();
        var handler = new ShashuRoutingHandler(
            "sk-trustgate-fake",
            inner,
            new ShashuRoutingOptions { BaseUrl = TestGateway.Uri });
        using var http = new HttpClient(handler);

        async Task Worker(string corpus, string canary)
        {
            Retrieval.DeclareRetrieval(new[] { canary }, corpus: corpus, query: canary);
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions");
            req.Content = JsonContent.Create(new { model = "m", messages = new[] { new { role = "user", content = "hi" } } });
            using var res = await http.SendAsync(req);
            Assert.True(res.IsSuccessStatusCode);
        }

        await Task.WhenAll(Worker("corpus_left", "LEFT"), Worker("corpus_right", "RIGHT"));
        foreach (var (corpus, canary) in new[] { ("corpus_left", "LEFT"), ("corpus_right", "RIGHT") })
        {
            var bodies = inner.Bodies[corpus];
            Assert.Equal(2, bodies.Count);
            Assert.Equal(bodies[0], bodies[1]);
            foreach (var raw in bodies)
            {
                var text = Encoding.UTF8.GetString(raw);
                Assert.Contains(corpus, text);
                Assert.Contains(canary, text);
                Assert.DoesNotContain(canary == "LEFT" ? "RIGHT" : "LEFT", text);
            }
        }
    }

    [Fact]
    public async Task C11_trace_and_session_frozen()
    {
        var cap = new CaptureStub(BusyThenOk);
        await Call(cap, maxRetries: 2);
        Assert.Equal(cap.Headers[0]["x-shashu-trace-id"], cap.Headers[1]["x-shashu-trace-id"]);
        Assert.Equal(cap.Headers[0]["x-shashu-session-id"], cap.Headers[1]["x-shashu-session-id"]);
    }

    private static HttpResponseMessage BusyThenOk(int n) => n == 1 ? Busy() : Ok();

    private static async Task Call(CaptureStub cap, int maxRetries)
    {
        using var http = new HttpClient(cap) { BaseAddress = new Uri("https://test.invalid/") };
        using var client = new ShashuClient(
            new ShashuConfig { BaseUrl = TestGateway.Uri, ApiKey = "tg_sk_same", MaxRetries = maxRetries }, http);
        await client.Chat.Completions.CreateAsync(Req("hi"));
    }

    private static ChatCompletionRequest Req(string text) =>
        new() { Model = "m", Messages = new[] { new ChatMessage("user", text) } };

    private static void AssertSameHeadersExceptAuth(CaptureStub cap)
    {
        static Dictionary<string, string> Strip(Dictionary<string, string> h) =>
            h.Where(kv => kv.Key is not "authorization").ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(Strip(cap.Headers[0]), Strip(cap.Headers[1]));
    }

    private static HttpResponseMessage Ok() =>
        new(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new
            {
                id = "chatcmpl-mock",
                choices = new[] { new { index = 0, message = new { role = "assistant", content = "ok" }, finish_reason = "stop" } },
            }),
        };

    private static HttpResponseMessage Busy() => Invited(HttpStatusCode.ServiceUnavailable);

    private static HttpResponseMessage Invited(HttpStatusCode status)
    {
        var r = new HttpResponseMessage(status)
        {
            Content = new StringContent(
                "{\"error\":{\"message\":\"" + BusySentence + "\",\"code\":\"gateway_busy\",\"shashu\":{\"decision\":\"fail\",\"code\":\"gateway_busy\"}}}"),
        };
        r.Headers.TryAddWithoutValidation("Retry-After", "0");
        r.Headers.TryAddWithoutValidation("x-should-retry", "true");
        return r;
    }

    private sealed class CaptureStub : HttpMessageHandler
    {
        private readonly Func<int, HttpResponseMessage> _next;
        public List<byte[]> Bodies { get; } = new();
        public List<Dictionary<string, string>> Headers { get; } = new();

        public CaptureStub(Func<int, HttpResponseMessage> next) => _next = next;

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
            return _next(Bodies.Count);
        }
    }

    private sealed class PerCorpusBusyThenOk : HttpMessageHandler
    {
        public ConcurrentDictionary<string, List<byte[]>> Bodies { get; } = new();
        private readonly ConcurrentDictionary<string, int> _n = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? Array.Empty<byte>()
                : await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            var corpus = doc.RootElement.GetProperty("shashu_metadata").GetProperty("rag_context").GetProperty("corpus").GetString()
                ?? "none";
            var bag = Bodies.GetOrAdd(corpus, _ => new List<byte[]>());
            lock (bag) bag.Add(body);
            var n = _n.AddOrUpdate(corpus, 1, (_, v) => v + 1);
            if (n == 1)
            {
                var busy = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    Content = new StringContent("{\"error\":{\"message\":\"busy\"}}"),
                };
                busy.Headers.TryAddWithoutValidation("Retry-After", "0");
                busy.Headers.TryAddWithoutValidation("x-should-retry", "true");
                return busy;
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"choices\":[]}") };
        }
    }
}
