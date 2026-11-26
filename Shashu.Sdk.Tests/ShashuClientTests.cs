using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using Shashu;
using Shashu.Chat;
using Xunit;
using Shashu.Tests.Harness;

namespace Shashu.Tests;

/// <summary>
/// Unit tests that exercise the client wiring without hitting a live
/// gateway. Uses a stub <see cref="HttpMessageHandler"/> so the test
/// suite stays hermetic and fast — integration tests against a real
/// gateway live in a separate test project (queued).
/// </summary>
public class ShashuClientTests
{
    [Fact]
    public void Ctor_WithoutApiKey_Throws()
    {
        Environment.SetEnvironmentVariable("SHASHU_API_KEY", null);
        Environment.SetEnvironmentVariable("SHASHU_API_KEY", null);
        var ex = Assert.Throws<ShashuConfigException>(() =>
            new ShashuClient(new ShashuConfig { BaseUrl = TestGateway.Uri, ApiKey = null }));
        Assert.Equal(CredentialResolver.BothOrNeither, ex.Message);
    }

    [Fact]
    public void Ctor_PicksUpEnvVarApiKey()
    {
        Environment.SetEnvironmentVariable("SHASHU_API_KEY", "sk-shashu-from-env");
        try
        {
            using var client = new ShashuClient(new ShashuConfig { BaseUrl = TestGateway.Uri });
            // No throw means env-var pickup worked. Concrete header-presence
            // assertions belong in the chat-completion test below.
            Assert.NotNull(client.Chat);
            Assert.NotNull(client.Chat.Completions);
        }
        finally
        {
            Environment.SetEnvironmentVariable("SHASHU_API_KEY", null);
        }
    }

    [Fact]
    public async Task ChatCompletions_HappyPath_RoundtripsResponse()
    {
        var stub = new StubHandler((req, _) =>
        {
            Assert.Equal(HttpMethod.Post, req.Method);
            Assert.Equal("/v1/chat/completions", req.RequestUri!.AbsolutePath);
            Assert.Equal("Bearer sk-shashu-test", req.Headers.Authorization!.ToString());
            Assert.Contains("shashu-dotnet/", req.Headers.UserAgent.ToString());

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new
                {
                    id = "chatcmpl-fake-1",
                    @object = "chat.completion",
                    created = 1_234_567_890,
                    model = "gpt-4o",
                    choices = new[]
                    {
                        new
                        {
                            index = 0,
                            message = new { role = "assistant", content = "hi back" },
                            finish_reason = "stop",
                        },
                    },
                    usage = new { prompt_tokens = 5, completion_tokens = 2, total_tokens = 7 },
                }),
            });
        });

        using var http = new HttpClient(stub) { BaseAddress = new Uri("https://test.invalid/") };
        using var client = new ShashuClient(
            new ShashuConfig { BaseUrl = TestGateway.Uri, ApiKey = "sk-shashu-test" }, http);

        var resp = await client.Chat.Completions.CreateAsync(new ChatCompletionRequest
        {
            Model = "gpt-4o",
            Messages = new[] { new ChatMessage("user", "hi") },
        });

        Assert.Equal("chatcmpl-fake-1", resp.Id);
        Assert.Single(resp.Choices);
        Assert.Equal("hi back", resp.Choices[0].Message.Content);
        Assert.NotNull(resp.Usage);
        Assert.Equal(7, resp.Usage!.TotalTokens);
    }

    // Note: the legacy `StreamingNotYetSupported_Throws` test was removed
    // when streaming landed in v0.2 work. The replacement assertion lives
    // in StreamingTests#CreateAsync_with_stream_true_throws_routing_caller_to_streaming_method
    // which checks that CreateAsync now throws InvalidOperationException
    // pointing at CreateStreamAsync instead of NotSupportedException.

    [Fact]
    public async Task ChatCompletions_5xx_NotRetried_ByDefault()
    {
        var calls = 0;
        var stub = new StubHandler((_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("{\"error\":{\"message\":\"boom\"}}"),
            });
        });
        using var http = new HttpClient(stub) { BaseAddress = new Uri("https://test.invalid/") };
        using var client = new ShashuClient(
            new ShashuConfig { BaseUrl = TestGateway.Uri, ApiKey = "sk-shashu-test", MaxRetries = 2 }, http);

        var ex = await Assert.ThrowsAsync<ShashuException>(() =>
            client.Chat.Completions.CreateAsync(new ChatCompletionRequest
            {
                Model = "gpt-4o",
                Messages = new[] { new ChatMessage("user", "hi") },
            }));
        Assert.Equal(500, ex.StatusCode);
        // 5xx is no longer retried by default — the gateway already retries providers.
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ChatCompletions_5xx_Retried_UnderLegacyPolicy()
    {
        var calls = 0;
        var stub = new StubHandler((_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("{\"error\":{\"message\":\"boom\"}}"),
            });
        });
        using var http = new HttpClient(stub) { BaseAddress = new Uri("https://test.invalid/") };
        using var client = new ShashuClient(
            new ShashuConfig
            {
                BaseUrl = TestGateway.Uri,
                ApiKey = "sk-shashu-test",
                MaxRetries = 1,
                LegacyRetryPolicy = true,
            }, http);

        var ex = await Assert.ThrowsAsync<ShashuException>(() =>
            client.Chat.Completions.CreateAsync(new ChatCompletionRequest
            {
                Model = "gpt-4o",
                Messages = new[] { new ChatMessage("user", "hi") },
            }));
        Assert.Equal(500, ex.StatusCode);
        Assert.Equal(2, calls); // 1 initial + 1 retry under the legacy policy
    }

    [Fact]
    public async Task ChatCompletions_429_Retried_ThenSucceeds()
    {
        var calls = 0;
        var stub = new StubHandler((_, _) =>
        {
            var n = Interlocked.Increment(ref calls);
            if (n == 1)
            {
                var r = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                {
                    Content = new StringContent("{\"error\":{\"type\":\"rate_limit_exceeded\",\"message\":\"slow\"}}"),
                };
                r.Headers.TryAddWithoutValidation("Retry-After", "0"); // honored; 0s → instant
                return Task.FromResult(r);
            }
            return Task.FromResult(OkCompletion());
        });
        using var http = new HttpClient(stub) { BaseAddress = new Uri("https://test.invalid/") };
        using var client = new ShashuClient(
            new ShashuConfig { BaseUrl = TestGateway.Uri, ApiKey = "sk-shashu-test", MaxRetries = 2 }, http);

        var resp = await client.Chat.Completions.CreateAsync(new ChatCompletionRequest
        {
            Model = "gpt-4o",
            Messages = new[] { new ChatMessage("user", "hi") },
        });
        Assert.Equal("chatcmpl-fake-1", resp.Id);
        Assert.Equal(2, calls); // 429 is retried by default
    }

    [Fact]
    public async Task ChatCompletions_ConnectionError_Retried_ThenSucceeds()
    {
        var calls = 0;
        var stub = new StubHandler((_, _) =>
        {
            var n = Interlocked.Increment(ref calls);
            if (n == 1)
            {
                throw new HttpRequestException(
                    "connection refused",
                    new SocketException((int)SocketError.ConnectionRefused));
            }
            return Task.FromResult(OkCompletion());
        });
        using var http = new HttpClient(stub) { BaseAddress = new Uri("https://test.invalid/") };
        using var client = new ShashuClient(
            new ShashuConfig { BaseUrl = TestGateway.Uri, ApiKey = "sk-shashu-test", MaxRetries = 2 }, http);

        var resp = await client.Chat.Completions.CreateAsync(new ChatCompletionRequest
        {
            Model = "gpt-4o",
            Messages = new[] { new ChatMessage("user", "hi") },
        });
        Assert.Equal("chatcmpl-fake-1", resp.Id);
        Assert.Equal(2, calls); // connection error retried then succeeded
    }

    [Fact]
    public async Task ChatCompletions_DropAfterSend_IsNotRetried()
    {
        var calls = 0;
        var stub = new StubHandler((_, _) =>
        {
            Interlocked.Increment(ref calls);
            throw new HttpRequestException(
                "The response ended prematurely.",
                new IOException("The response ended prematurely."));
        });
        using var http = new HttpClient(stub) { BaseAddress = new Uri("https://test.invalid/") };
        using var client = new ShashuClient(
            new ShashuConfig { BaseUrl = TestGateway.Uri, ApiKey = "sk-shashu-test", MaxRetries = 2 }, http);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.Chat.Completions.CreateAsync(new ChatCompletionRequest
            {
                Model = "gpt-4o",
                Messages = new[] { new ChatMessage("user", "hi") },
            }));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ChatCompletions_403_NotRetried()
    {
        var calls = 0;
        var stub = new StubHandler((_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent(
                    "{\"error\":{\"message\":\"blocked by policy\",\"type\":\"invalid_request_error\"}}"),
            });
        });
        using var http = new HttpClient(stub) { BaseAddress = new Uri("https://test.invalid/") };
        using var client = new ShashuClient(
            new ShashuConfig { BaseUrl = TestGateway.Uri, ApiKey = "sk-shashu-test", MaxRetries = 2 }, http);

        await Assert.ThrowsAsync<ShashuPolicyViolation>(() =>
            client.Chat.Completions.CreateAsync(new ChatCompletionRequest
            {
                Model = "gpt-4o",
                Messages = new[] { new ChatMessage("user", "hi") },
            }));
        Assert.Equal(1, calls); // 403 is never retried
    }

    private static HttpResponseMessage OkCompletion() =>
        new(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new
            {
                id = "chatcmpl-fake-1",
                @object = "chat.completion",
                created = 1_234_567_890,
                model = "gpt-4o",
                choices = new[]
                {
                    new
                    {
                        index = 0,
                        message = new { role = "assistant", content = "hi back" },
                        finish_reason = "stop",
                    },
                },
                usage = new { prompt_tokens = 5, completion_tokens = 2, total_tokens = 7 },
            }),
        };
}

/// <summary>Minimal HttpMessageHandler that delegates to a callback.</summary>
internal sealed class StubHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

    public StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        => _handler = handler;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => _handler(request, cancellationToken);
}
