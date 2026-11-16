using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Shashu;
using Shashu.Chat;
using Shashu.Tests.Harness;
using Xunit;

namespace Shashu.Tests;

public class IdentityCloudPassTests
{
    private const string ExpiredSentence = "This pass has expired. Fetch a new one.";

    private static string Mint(long lifetimeSeconds)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var header = B64("{\"alg\":\"none\"}");
        var payload = B64($"{{\"exp\":{now + lifetimeSeconds},\"iat\":{now}}}");
        return header + "." + payload + ".sig";
    }

    private static string B64(string s) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Refusal(string code, string message) =>
        $"{{\"error\":{{\"message\":\"{message}\",\"type\":\"invalid_request_error\",\"code\":\"{code}\"}}}}";

    private static async Task Chat(ShashuClient client) =>
        await client.Chat.Completions.CreateAsync(new ChatCompletionRequest
        {
            Model = "mock",
            Messages = [new ChatMessage("user", "hi")],
        });

    [Fact]
    public void Both_or_neither()
    {
        var ex = Assert.Throws<ShashuConfigException>(() =>
            new ShashuClient(new ShashuConfig
            {
                BaseUrl = TestGateway.Uri,
                ApiKey = "tg_sk_x",
                TokenProvider = _ => ValueTask.FromResult("p"),
            }));
        Assert.Equal(CredentialResolver.BothOrNeither, ex.Message);
    }

    [Fact]
    public async Task Reuse_until_skew()
    {
        var token = Mint(300);
        var n = 0;
        var stub = QueueStub(Ok(), Ok());
        using var http = new HttpClient(stub) { BaseAddress = TestGateway.Uri };
        using var client = new ShashuClient(new ShashuConfig
        {
            BaseUrl = TestGateway.Uri,
            MaxRetries = 0,
            TokenProvider = _ =>
            {
                Interlocked.Increment(ref n);
                return ValueTask.FromResult(token);
            },
        }, http);
        await Chat(client);
        await Chat(client);
        Assert.Equal(1, n);
        Assert.All(stub.Auth, a => Assert.Equal("Bearer " + token, a));
    }

    [Fact]
    public async Task Refresh_at_exp_minus_60s()
    {
        var first = Mint(50);
        var second = Mint(300);
        var n = 0;
        var stub = QueueStub(Ok(), Ok());
        using var http = new HttpClient(stub) { BaseAddress = TestGateway.Uri };
        using var client = new ShashuClient(new ShashuConfig
        {
            BaseUrl = TestGateway.Uri,
            MaxRetries = 0,
            TokenProvider = _ =>
            {
                var i = Interlocked.Increment(ref n);
                return ValueTask.FromResult(i == 1 ? first : second);
            },
        }, http);
        await Chat(client);
        await Chat(client);
        Assert.Equal(2, n);
        Assert.Equal("Bearer " + first, stub.Auth[0]);
        Assert.Equal("Bearer " + second, stub.Auth[1]);
    }

    [Fact]
    public async Task Retry_once_on_pass_expired()
    {
        var n = 0;
        var stub = QueueStub(
            Json(HttpStatusCode.Unauthorized, Refusal(Shashu.PassExpired, ExpiredSentence)),
            Ok());
        using var http = new HttpClient(stub) { BaseAddress = TestGateway.Uri };
        using var client = new ShashuClient(new ShashuConfig
        {
            BaseUrl = TestGateway.Uri,
            MaxRetries = 0,
            TokenProvider = _ =>
            {
                Interlocked.Increment(ref n);
                return ValueTask.FromResult(Mint(300));
            },
        }, http);
        await Chat(client);
        Assert.Equal(2, n);
        Assert.Equal(2, stub.Count);
    }

    [Theory]
    [InlineData(CredentialResolver.PASS_PROVIDER_NOT_SET_UP, "refused")]
    [InlineData(CredentialResolver.PASS_NOT_LICENSED, "refused")]
    [InlineData(CredentialResolver.PASS_WRONG_AUDIENCE, "This pass is meant for a different audience.")]
    [InlineData(CredentialResolver.PASS_WRONG_ISSUER, "refused")]
    [InlineData(CredentialResolver.PASS_UNKNOWN_AGENT, "No agent matches this pass.")]
    public async Task Other_pass_codes_no_retry(string code, string sentence)
    {
        var n = 0;
        var stub = QueueStub(Json(HttpStatusCode.Unauthorized, Refusal(code, sentence)));
        using var http = new HttpClient(stub) { BaseAddress = TestGateway.Uri };
        using var client = new ShashuClient(new ShashuConfig
        {
            BaseUrl = TestGateway.Uri,
            MaxRetries = 0,
            TokenProvider = _ =>
            {
                Interlocked.Increment(ref n);
                return ValueTask.FromResult(Mint(300));
            },
        }, http);
        var ex = await Assert.ThrowsAsync<ShashuAuthenticationException>(() => Chat(client));
        Assert.Equal(code, ex.Code);
        Assert.Equal(sentence, ex.Message);
        Assert.Equal(1, n);
        Assert.Equal(1, stub.Count);
    }

    [Fact]
    public async Task Key_mode_no_retry_on_pass_expired()
    {
        var stub = QueueStub(Json(HttpStatusCode.Unauthorized, Refusal(Shashu.PassExpired, ExpiredSentence)));
        using var http = new HttpClient(stub) { BaseAddress = TestGateway.Uri };
        using var client = new ShashuClient(new ShashuConfig
        {
            BaseUrl = TestGateway.Uri,
            ApiKey = "tg_sk_test",
            MaxRetries = 0,
        }, http);
        var ex = await Assert.ThrowsAsync<ShashuAuthenticationException>(() => Chat(client));
        Assert.Equal(ExpiredSentence, ex.Message);
        Assert.Equal(1, stub.Count);
    }

    [Fact]
    public async Task Missing_code_is_not_retried()
    {
        var n = 0;
        var stub = QueueStub(Json(HttpStatusCode.Unauthorized,
            "{\"error\":{\"message\":\"Not authenticated\",\"type\":\"invalid_request_error\"}}"));
        using var http = new HttpClient(stub) { BaseAddress = TestGateway.Uri };
        using var client = new ShashuClient(new ShashuConfig
        {
            BaseUrl = TestGateway.Uri,
            MaxRetries = 0,
            TokenProvider = _ =>
            {
                Interlocked.Increment(ref n);
                return ValueTask.FromResult(Mint(300));
            },
        }, http);
        var ex = await Assert.ThrowsAsync<ShashuAuthenticationException>(() => Chat(client));
        Assert.Equal("Not authenticated", ex.Message);
        Assert.Equal(1, n);
        Assert.Equal(1, stub.Count);
    }

    [Fact]
    public void Six_codes_are_named_constants()
    {
        Assert.Equal(
            new HashSet<string>
            {
                "pass_provider_not_set_up", "pass_not_licensed", "pass_expired",
                "pass_wrong_audience", "pass_wrong_issuer", "pass_unknown_agent",
            },
            Shashu.PassCodes.ToHashSet());
    }

    [Fact]
    public void No_public_type_named_TrustGate()
    {
        foreach (var t in typeof(ShashuClient).Assembly.GetExportedTypes())
            Assert.DoesNotContain("TrustGate", t.Name, StringComparison.Ordinal);
    }

    [Fact]
    public async Task One_refresh_under_fifty_concurrent()
    {
        var token = Mint(300);
        var n = 0;
        var responses = Enumerable.Range(0, 50).Select(_ => Ok()).ToArray();
        var stub = QueueStub(responses);
        using var http = new HttpClient(stub) { BaseAddress = TestGateway.Uri };
        using var client = new ShashuClient(new ShashuConfig
        {
            BaseUrl = TestGateway.Uri,
            MaxRetries = 0,
            TokenProvider = _ =>
            {
                Interlocked.Increment(ref n);
                return ValueTask.FromResult(token);
            },
        }, http);
        await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => Chat(client)));
        Assert.Equal(1, n);
    }

    [Fact]
    public void Streaming_does_not_retry_after_first_byte()
    {
        Assert.False(CredentialResolver.ShouldRetryPassRefused(true, 401, Shashu.PassExpired, true));
        Assert.True(CredentialResolver.ShouldRetryPassRefused(true, 401, Shashu.PassExpired, false));
    }

    private static HttpResponseMessage Ok() =>
        new(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new
            {
                id = "chatcmpl-mock",
                @object = "chat.completion",
                choices = new[]
                {
                    new { index = 0, message = new { role = "assistant", content = "ok" }, finish_reason = "stop" },
                },
            }),
        };

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static RecordingStub QueueStub(params HttpResponseMessage[] responses) =>
        new(responses);

    private sealed class RecordingStub : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _q;
        public List<string?> Auth { get; } = new();
        public int Count { get; private set; }

        public RecordingStub(IEnumerable<HttpResponseMessage> responses) =>
            _q = new Queue<HttpResponseMessage>(responses);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Auth.Add(request.Headers.Authorization?.ToString());
            Count++;
            return Task.FromResult(_q.Dequeue());
        }
    }
}
