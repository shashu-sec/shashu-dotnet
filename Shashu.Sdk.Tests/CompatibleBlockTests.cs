using System.Net;
using System.Text;
using Shashu;
using Shashu.Chat;
using Xunit;
using Shashu.Tests.Harness;

namespace Shashu.Tests;

/// <summary>
/// The 200-encoded block. This SDK sends <c>shashu-version: 2026-09-01</c>
/// by default, where a block is a 400; the 200 shape is still what the gateway
/// serves on the <c>compatible</c> encoding, or on the legacy contract to a
/// client that sets <see cref="ShashuConfig.ContractVersion"/> to
/// null — and there it must still throw.
/// Body is <c>tests/golden_wire/chat_blocked_content_filter.golden.json</c>.
/// </summary>
public class CompatibleBlockTests
{
    private const string Blocked200 =
        "{\"id\":\"chatcmpl-deadbeefcafe4321aabbccdd\",\"object\":\"chat.completion\","
        + "\"created\":1750000000,\"model\":\"gpt-4o\",\"choices\":[{\"index\":0,"
        + "\"message\":{\"role\":\"assistant\",\"content\":\"I cannot fulfill this request "
        + "because it violates the organization's security policy.\"},"
        + "\"finish_reason\":\"content_filter\"}],"
        + "\"usage\":{\"prompt_tokens\":0,\"completion_tokens\":0,\"total_tokens\":0}}";

    [Fact]
    public async Task CreateAsync_200_content_filter_throws()
    {
        using var client = BuildClient(Blocked200);

        var ex = await Assert.ThrowsAsync<ShashuContentFilterException>(() =>
            client.Chat.Completions.CreateAsync(new ChatCompletionRequest
            {
                Model = "gpt-4o",
                Messages = new[] { new ChatMessage("user", "hi") },
            }));

        Assert.Equal(200, ex.StatusCode);
        Assert.Equal("content_filter", ex.ErrorType);
        Assert.Contains("security policy", ex.Message);
    }

    [Fact]
    public async Task CreateAsync_200_stop_does_not_throw()
    {
        using var client = BuildClient(
            "{\"id\":\"c1\",\"object\":\"chat.completion\",\"created\":1,\"model\":\"gpt-4o\","
            + "\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"ok\"},"
            + "\"finish_reason\":\"stop\"}]}");

        var resp = await client.Chat.Completions.CreateAsync(new ChatCompletionRequest
        {
            Model = "gpt-4o",
            Messages = new[] { new ChatMessage("user", "hi") },
        });

        Assert.Equal("stop", resp.Choices[0].FinishReason);
        Assert.Equal("ok", resp.Choices[0].Message.Content);
    }

    /// <summary>
    /// The same live allowed body as the streamed capture, buffered:
    /// <c>"created": null</c>. A non-nullable <c>long</c> throws
    /// <c>JsonException</c> on a clean reply. Proven red against
    /// <c>ChatCompletionResponse.Created</c> as <c>long</c>.
    /// </summary>
    [Fact]
    public async Task CreateAsync_allowed_reply_with_created_null_does_not_throw()
    {
        using var client = BuildClient(
            "{\"id\":\"chatcmpl-fab1e5c0ffee\",\"object\":\"chat.completion\","
            + "\"created\":null,\"model\":\"gpt-4o\","
            + "\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\","
            + "\"content\":\"The capital of France is Paris.\"},"
            + "\"finish_reason\":\"stop\"}]}");

        var resp = await client.Chat.Completions.CreateAsync(new ChatCompletionRequest
        {
            Model = "gpt-4o",
            Messages = new[] { new ChatMessage("user", "hi") },
        });

        Assert.Null(resp.Created);
        Assert.Equal("The capital of France is Paris.", resp.Choices[0].Message.Content);
        Assert.Equal("stop", resp.Choices[0].FinishReason);
    }

    private static ShashuClient BuildClient(string body)
    {
        var handler = new JsonStubHandler(body);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://test.invalid/") };
        return new ShashuClient(new ShashuConfig { BaseUrl = TestGateway.Uri, ApiKey = "sk-trustgate-test" }, http);
    }

    private sealed class JsonStubHandler : HttpMessageHandler
    {
        private readonly string _body;
        public JsonStubHandler(string body) => _body = body;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
