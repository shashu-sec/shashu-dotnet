using System.Net;
using System.Text;
using System.Text.Json;
using Shashu;
using Shashu.Chat;
using Shashu.Tests.Harness;
using Xunit;

namespace Shashu.Tests;

/// <summary>IDENTITY I5 / Python identity S1: cloud-pass sign-in.</summary>
[Collection(EnvironmentCollection.Name)]
public class IdentityI5Tests : IDisposable
{
    private static readonly Dictionary<string, string> Sentence = new()
    {
        ["pass_provider_not_set_up"] = "No identity provider is set up for agents.",
        ["pass_not_licensed"] = "Pass sign-in needs the Enterprise plan.",
        ["pass_expired"] = "This pass has expired. Fetch a new one.",
        ["pass_wrong_audience"] = "This pass is meant for a different audience.",
        ["pass_wrong_issuer"] = "This pass comes from an unknown issuer.",
        ["pass_unknown_agent"] = "No agent matches this pass.",
    };

    public IdentityI5Tests()
    {
        Environment.SetEnvironmentVariable("SHASHU_API_KEY", null);
        Environment.SetEnvironmentVariable("SHASHU_API_KEY", null);
        CredentialResolver.UnixNow = static () => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }

    public void Dispose() =>
        CredentialResolver.UnixNow = static () => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    [Fact]
    public void Config_has_TokenProvider()
    {
        Assert.NotNull(typeof(ShashuConfig).GetProperty("TokenProvider"));
    }

    [Fact]
    public void HttpClient_does_not_bake_Authorization_at_construction()
    {
        using var http = new HttpClient();
        using var client = new ShashuClient(
            new ShashuConfig { BaseUrl = new Uri("http://127.0.0.1:9"), ApiKey = "shashu_sk_test" },
            http);
        Assert.Null(http.DefaultRequestHeaders.Authorization);
    }

    [Fact]
    public void Both_or_neither_throws()
    {
        var both = Assert.Throws<ShashuConfigException>(() =>
            new ShashuClient(new ShashuConfig
            {
                BaseUrl = TestGateway.Uri,
                ApiKey = "shashu_sk_x",
                TokenProvider = _ => new ValueTask<string>("p"),
            }));
        Assert.Equal(CredentialResolver.BothOrNeither, both.Message);

        var neither = Assert.Throws<ShashuConfigException>(() =>
            new ShashuClient(new ShashuConfig { BaseUrl = TestGateway.Uri }));
        Assert.Equal(CredentialResolver.BothOrNeither, neither.Message);
    }

    [Fact]
    public async Task Pass_is_reused_until_skew()
    {
        var minted = PassMint.Mint(300);
        CredentialResolver.UnixNow = () => minted.Iat;
        var calls = 0;
        var seen = new List<string?>();
        using var http = new HttpClient(new StubHandler((req, _) =>
        {
            seen.Add(req.Headers.Authorization?.ToString());
            return Task.FromResult(OkChat());
        }))
        { BaseAddress = TestGateway.Uri };
        using var tg = new ShashuClient(new ShashuConfig
        {
            BaseUrl = TestGateway.Uri,
            TokenProvider = _ =>
            {
                Interlocked.Increment(ref calls);
                return new ValueTask<string>(minted.Token);
            },
            MaxRetries = 0,
        }, http);

        await Create(tg);
        await Create(tg);
        Assert.Equal(1, calls);
        Assert.Equal(new[] { $"Bearer {minted.Token}", $"Bearer {minted.Token}" }, seen);
    }

    [Fact]
    public async Task Refresh_at_exp_minus_60s()
    {
        var first = PassMint.Mint(90);
        var second = PassMint.Mint(300, first.Iat + 10);
        var tokens = new[] { first.Token, second.Token };
        var calls = 0;
        var now = first.Iat;
        CredentialResolver.UnixNow = () => now;
        var seen = new List<string?>();
        using var http = new HttpClient(new StubHandler((req, _) =>
        {
            seen.Add(req.Headers.Authorization?.ToString());
            return Task.FromResult(OkChat());
        }))
        { BaseAddress = TestGateway.Uri };
        using var tg = new ShashuClient(new ShashuConfig
        {
            BaseUrl = TestGateway.Uri,
            TokenProvider = _ =>
            {
                var token = tokens[Math.Min(calls, 1)];
                calls++;
                return new ValueTask<string>(token);
            },
            MaxRetries = 0,
        }, http);

        await Create(tg);
        now = first.Exp - 59;
        await Create(tg);
        Assert.Equal(2, calls);
        Assert.Equal($"Bearer {first.Token}", seen[0]);
        Assert.Equal($"Bearer {second.Token}", seen[1]);
    }

    [Fact]
    public async Task One_refresh_under_50_concurrent()
    {
        var minted = PassMint.Mint(300);
        var calls = 0;
        var barrier = new Barrier(50);
        using var http = new HttpClient(new StubHandler((_, _) => Task.FromResult(OkChat())))
        {
            BaseAddress = TestGateway.Uri,
        };
        using var tg = new ShashuClient(new ShashuConfig
        {
            BaseUrl = TestGateway.Uri,
            TokenProvider = async ct =>
            {
                Interlocked.Increment(ref calls);
                await Task.Delay(50, ct);
                return minted.Token;
            },
            MaxRetries = 0,
        }, http);

        var tasks = Enumerable.Range(0, 50).Select(_ => Task.Run(async () =>
        {
            barrier.SignalAndWait();
            await Create(tg);
        })).ToArray();
        await Task.WhenAll(tasks);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Retry_once_on_pass_expired()
    {
        var first = PassMint.Mint(300);
        var second = PassMint.Mint(300, first.Iat + 1);
        var tokens = new[] { first.Token, second.Token };
        var i = 0;
        var plan = new Queue<HttpResponseMessage>();
        plan.Enqueue(JsonStatus(HttpStatusCode.Unauthorized, Refusal("pass_expired")));
        plan.Enqueue(OkChat());
        var auths = new List<string?>();
        using var http = new HttpClient(new StubHandler((req, _) =>
        {
            auths.Add(req.Headers.Authorization?.ToString());
            return Task.FromResult(plan.Dequeue());
        }))
        { BaseAddress = TestGateway.Uri };
        using var tg = new ShashuClient(new ShashuConfig
        {
            BaseUrl = TestGateway.Uri,
            TokenProvider = _ => new ValueTask<string>(tokens[Math.Min(i++, 1)]),
            MaxRetries = 0,
        }, http);

        var outResp = await Create(tg);
        Assert.Equal("ok", outResp.Choices[0].Message.Content);
        Assert.Equal(2, i);
        Assert.Equal(2, auths.Count);
        Assert.NotEqual(auths[0], auths[1]);
    }

    [Theory]
    [InlineData("pass_provider_not_set_up")]
    [InlineData("pass_not_licensed")]
    [InlineData("pass_wrong_audience")]
    [InlineData("pass_wrong_issuer")]
    [InlineData("pass_unknown_agent")]
    public async Task Other_pass_codes_raise_with_code_and_exact_message_no_retry(string code)
    {
        var first = PassMint.Mint(300);
        var i = 0;
        using var http = new HttpClient(new StubHandler((_, _) =>
            Task.FromResult(JsonStatus(HttpStatusCode.Unauthorized, Refusal(code)))))
        {
            BaseAddress = TestGateway.Uri,
        };
        using var tg = new ShashuClient(new ShashuConfig
        {
            BaseUrl = TestGateway.Uri,
            TokenProvider = _ =>
            {
                i++;
                return new ValueTask<string>(first.Token);
            },
            MaxRetries = 0,
        }, http);

        var ex = await Assert.ThrowsAsync<ShashuAuthenticationException>(() => Create(tg));
        Assert.Equal(code, ex.Code);
        Assert.Equal(Sentence[code], ex.Message);
        Assert.Equal(1, i);
    }

    [Fact]
    public async Task Expired_twice_retries_only_once()
    {
        var n = 0;
        var plan = new Queue<HttpResponseMessage>();
        plan.Enqueue(JsonStatus(HttpStatusCode.Unauthorized, Refusal("pass_expired")));
        plan.Enqueue(JsonStatus(HttpStatusCode.Unauthorized, Refusal("pass_expired")));
        using var http = new HttpClient(new StubHandler((_, _) => Task.FromResult(plan.Dequeue())))
        {
            BaseAddress = TestGateway.Uri,
        };
        using var tg = new ShashuClient(new ShashuConfig
        {
            BaseUrl = TestGateway.Uri,
            TokenProvider = _ =>
            {
                n++;
                return new ValueTask<string>(PassMint.Mint(300).Token);
            },
            MaxRetries = 0,
        }, http);

        var ex = await Assert.ThrowsAsync<ShashuAuthenticationException>(() => Create(tg));
        Assert.Equal("pass_expired", ex.Code);
        Assert.Equal(Sentence["pass_expired"], ex.Message);
        Assert.Equal(2, n);
    }

    [Fact]
    public async Task Older_gateway_without_code_is_not_retried()
    {
        var n = 0;
        using var http = new HttpClient(new StubHandler((_, _) =>
            Task.FromResult(JsonStatus(HttpStatusCode.Unauthorized,
                """{"error":{"message":"This pass has expired. Fetch a new one.","type":"invalid_request_error"}}"""))))
        {
            BaseAddress = TestGateway.Uri,
        };
        using var tg = new ShashuClient(new ShashuConfig
        {
            BaseUrl = TestGateway.Uri,
            TokenProvider = _ =>
            {
                n++;
                return new ValueTask<string>(PassMint.Mint(300).Token);
            },
            MaxRetries = 0,
        }, http);
        var ex = await Assert.ThrowsAsync<ShashuAuthenticationException>(() => Create(tg));
        Assert.Equal("This pass has expired. Fetch a new one.", ex.Message);
        Assert.Equal(1, n);
    }

    [Fact]
    public async Task Legacy_status_code_body_is_not_retried()
    {
        var n = 0;
        using var http = new HttpClient(new StubHandler((_, _) =>
            Task.FromResult(JsonStatus(HttpStatusCode.Unauthorized,
                """{"error":{"message":"This pass has expired. Fetch a new one.","type":"invalid_request_error","code":"401"}}"""))))
        {
            BaseAddress = TestGateway.Uri,
        };
        using var tg = new ShashuClient(new ShashuConfig
        {
            BaseUrl = TestGateway.Uri,
            TokenProvider = _ =>
            {
                n++;
                return new ValueTask<string>(PassMint.Mint(300).Token);
            },
            MaxRetries = 0,
        }, http);
        await Assert.ThrowsAsync<ShashuAuthenticationException>(() => Create(tg));
        Assert.Equal(1, n);
    }

    [Fact]
    public async Task Key_mode_never_retries_even_on_pass_expired()
    {
        var calls = 0;
        using var http = new HttpClient(new StubHandler((_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(JsonStatus(HttpStatusCode.Unauthorized, Refusal("pass_expired")));
        }))
        { BaseAddress = TestGateway.Uri };
        using var tg = new ShashuClient(new ShashuConfig
        {
            BaseUrl = TestGateway.Uri,
            ApiKey = "shashu_sk_test",
            MaxRetries = 0,
        }, http);
        var ex = await Assert.ThrowsAsync<ShashuAuthenticationException>(() => Create(tg));
        Assert.Equal("pass_expired", ex.Code);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Key_mode_sends_static_bearer()
    {
        string? auth = null;
        using var http = new HttpClient(new StubHandler((req, _) =>
        {
            auth = req.Headers.Authorization?.ToString();
            return Task.FromResult(OkChat());
        }))
        { BaseAddress = TestGateway.Uri };
        using var tg = new ShashuClient(new ShashuConfig
        {
            BaseUrl = TestGateway.Uri,
            ApiKey = "shashu_sk_test",
        }, http);
        await Create(tg);
        Assert.Equal("Bearer shashu_sk_test", auth);
    }

    [Fact]
    public async Task Streaming_retries_on_pass_expired_before_any_byte()
    {
        var i = 0;
        var tokens = new[] { PassMint.Mint(300).Token, PassMint.Mint(300, 1_700_000_100).Token };
        var plan = new Queue<HttpResponseMessage>();
        plan.Enqueue(JsonStatus(HttpStatusCode.Unauthorized, Refusal("pass_expired")));
        plan.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "data: {\"choices\":[{\"delta\":{\"content\":\"x\"}}]}\n\n",
                Encoding.UTF8, "text/event-stream"),
        });
        using var http = new HttpClient(new StubHandler((_, _) => Task.FromResult(plan.Dequeue())))
        {
            BaseAddress = TestGateway.Uri,
        };
        using var tg = new ShashuClient(new ShashuConfig
        {
            BaseUrl = TestGateway.Uri,
            TokenProvider = _ => new ValueTask<string>(tokens[Math.Min(i++, 1)]),
            MaxRetries = 0,
        }, http);

        var chunks = new List<ChatCompletionChunk>();
        await foreach (var chunk in tg.Chat.Completions.CreateStreamAsync(new ChatCompletionRequest
        {
            Model = "mock",
            Messages = [new ChatMessage("user", "hi")],
            Stream = true,
        }))
        {
            chunks.Add(chunk);
        }
        Assert.Equal(2, i);
        Assert.NotEmpty(chunks);
    }

    [Fact]
    public void No_retry_once_bytes_have_arrived()
    {
        Assert.False(CredentialResolver.ShouldRetryPassRefused(true, 401, "pass_expired", true));
        Assert.True(CredentialResolver.ShouldRetryPassRefused(true, 401, "pass_expired", false));
    }

    [Fact]
    public void The_six_codes_are_named_constants()
    {
        Assert.Equal(Sentence.Keys.ToArray(), CredentialResolver.PASS_CODES);
        Assert.Equal("pass_expired", CredentialResolver.PASS_EXPIRED);
        Assert.Equal("pass_provider_not_set_up", CredentialResolver.PASS_PROVIDER_NOT_SET_UP);
        Assert.Equal("pass_not_licensed", CredentialResolver.PASS_NOT_LICENSED);
        Assert.Equal("pass_wrong_audience", CredentialResolver.PASS_WRONG_AUDIENCE);
        Assert.Equal("pass_wrong_issuer", CredentialResolver.PASS_WRONG_ISSUER);
        Assert.Equal("pass_unknown_agent", CredentialResolver.PASS_UNKNOWN_AGENT);
    }

    [Fact]
    public void No_public_type_name_contains_TrustGate()
    {
        var hits = typeof(ShashuClient).Assembly.GetExportedTypes()
            .Select(t => t.FullName!)
            .Where(n => n.Contains("TrustGate", StringComparison.Ordinal))
            .ToArray();
        Assert.Empty(hits);
    }

    private static async Task<ChatCompletionResponse> Create(ShashuClient tg) =>
        await tg.Chat.Completions.CreateAsync(new ChatCompletionRequest
        {
            Model = "mock",
            Messages = [new ChatMessage("user", "hi")],
        });

    private static HttpResponseMessage OkChat() =>
        JsonStatus(HttpStatusCode.OK,
            """{"id":"chatcmpl-mock","object":"chat.completion","choices":[{"index":0,"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}]}""");

    private static string Refusal(string code) =>
        JsonSerializer.Serialize(new
        {
            error = new
            {
                message = Sentence[code],
                type = "invalid_request_error",
                param = (string?)null,
                code,
            },
        });

    private static HttpResponseMessage JsonStatus(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

}

internal static class PassMint
{
    internal static (string Token, long Iat, long Exp) Mint(int lifetimeS, long? now = null)
    {
        var iat = now ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var exp = iat + lifetimeS;
        static string B64(string json)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }
        var token = $"{B64("{\"alg\":\"none\"}")}.{B64($"{{\"iat\":{iat},\"exp\":{exp}}}")}.sig";
        return (token, iat, exp);
    }
}
