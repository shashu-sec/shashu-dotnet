using System.Net;
using System.Text;
using Shashu;
using Shashu.Chat;
using Xunit;
using Shashu.Tests.Harness;

namespace Shashu.Tests;

/// <summary>
/// Hermetic tests for streaming chat-completions. Stubs an SSE-emitting
/// HttpMessageHandler and verifies the SDK's parser surfaces chunks
/// correctly, handles the <c>[DONE]</c> sentinel, and surfaces
/// non-2xx + mid-stream errors.
/// </summary>
public class StreamingTests
{
    // Captured verbatim from the live gateway's ALLOWED streaming path
    // (docs/contracts/PARKED-SDK-WORK.md §3.1): created is JSON null.
    // _graceful_security_response writes a real integer, so a BLOCK parses
    // and a normal reply throws JsonException → ShashuException.
    // The rest of the frame is the enforced chat SSE grammar
    // (tests/golden_wire_stream/chat_allowed_basic-usage0.sse).
    private const string LiveAllowedStreamCreatedNull =
        "data: {\"id\":\"chatcmpl-fab1e5c0ffee\",\"object\":\"chat.completion.chunk\",\"created\":null,\"model\":\"gpt-4o\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"\"},\"finish_reason\":null}]}\n\n" +
        "data: {\"id\":\"chatcmpl-fab1e5c0ffee\",\"object\":\"chat.completion.chunk\",\"created\":null,\"model\":\"gpt-4o\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"The capital of France is Paris.\"},\"finish_reason\":null}]}\n\n" +
        "data: {\"id\":\"chatcmpl-fab1e5c0ffee\",\"object\":\"chat.completion.chunk\",\"created\":null,\"model\":\"gpt-4o\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n" +
        "data: [DONE]\n\n";

    private const string LiveBlockedStreamCreatedInt =
        "data: {\"id\":\"chatcmpl-deadbeefcafe4321aabbccdd\",\"object\":\"chat.completion.chunk\",\"created\":1750000000,\"model\":\"gpt-4o\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"\"},\"finish_reason\":null}]}\n\n" +
        "data: {\"id\":\"chatcmpl-deadbeefcafe4321aabbccdd\",\"object\":\"chat.completion.chunk\",\"created\":1750000000,\"model\":\"gpt-4o\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"I cannot fulfill this request because it \"},\"finish_reason\":null}]}\n\n" +
        "data: {\"id\":\"chatcmpl-deadbeefcafe4321aabbccdd\",\"object\":\"chat.completion.chunk\",\"created\":1750000000,\"model\":\"gpt-4o\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"violates the organization's security policy.\"},\"finish_reason\":null}]}\n\n" +
        "data: {\"id\":\"chatcmpl-deadbeefcafe4321aabbccdd\",\"object\":\"chat.completion.chunk\",\"created\":1750000000,\"model\":\"gpt-4o\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"content_filter\"}]}\n\n" +
        "data: [DONE]\n\n";

    [Fact]
    public async Task Stream_allowed_reply_with_created_null_does_not_throw()
    {
        using var client = BuildClient(sseBody: LiveAllowedStreamCreatedNull, status: HttpStatusCode.OK);

        var collected = new List<ChatCompletionChunk>();
        await foreach (var chunk in client.Chat.Completions.CreateStreamAsync(
            new ChatCompletionRequest
            {
                Model = "gpt-4o",
                Messages = new[] { new ChatMessage("user", "hi") },
                Stream = true,
            }))
        {
            collected.Add(chunk);
        }

        Assert.Equal(3, collected.Count);
        Assert.Null(collected[0].Created);
        Assert.Equal("The capital of France is Paris.", collected[1].Choices[0].Delta.Content);
        Assert.Equal("stop", collected[2].Choices[0].FinishReason);
    }

    [Fact]
    public async Task Stream_blocked_reply_with_created_integer_throws_content_filter()
    {
        using var client = BuildClient(sseBody: LiveBlockedStreamCreatedInt, status: HttpStatusCode.OK);

        var ex = await Assert.ThrowsAsync<ShashuContentFilterException>(async () =>
        {
            await foreach (var _ in client.Chat.Completions.CreateStreamAsync(
                new ChatCompletionRequest
                {
                    Model = "gpt-4o",
                    Messages = new[] { new ChatMessage("user", "hi") },
                    Stream = true,
                }))
            { }
        });

        Assert.Equal(200, ex.StatusCode);
        Assert.Equal("content_filter", ex.ErrorType);
    }

    [Fact]
    public async Task Stream_yields_each_data_chunk_then_stops_at_DONE()
    {
        var sse =
            "data: {\"id\":\"c1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"gpt-4o\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\"},\"finish_reason\":null}]}\n\n" +
            "data: {\"id\":\"c1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"gpt-4o\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"Hel\"},\"finish_reason\":null}]}\n\n" +
            "data: {\"id\":\"c1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"gpt-4o\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"lo\"},\"finish_reason\":\"stop\"}]}\n\n" +
            "data: [DONE]\n\n";

        using var client = BuildClient(sseBody: sse, status: HttpStatusCode.OK);

        var collected = new List<ChatCompletionChunk>();
        await foreach (var chunk in client.Chat.Completions.CreateStreamAsync(
            new ChatCompletionRequest
            {
                Model = "gpt-4o",
                Messages = new[] { new ChatMessage("user", "hi") },
                Stream = true,
            }))
        {
            collected.Add(chunk);
        }

        Assert.Equal(3, collected.Count);
        Assert.Equal("assistant", collected[0].Choices[0].Delta.Role);
        Assert.Equal("Hel", collected[1].Choices[0].Delta.Content);
        Assert.Equal("lo", collected[2].Choices[0].Delta.Content);
        Assert.Equal("stop", collected[2].Choices[0].FinishReason);
    }

    [Fact]
    public async Task Stream_force_sets_stream_true_even_if_caller_omitted_it()
    {
        // Callers might pass Stream=null/false to CreateStreamAsync; the SDK
        // must override to true so the gateway opens an SSE channel.
        var bodyCapture = new BodyCapturingHandler(
            "data: {\"id\":\"c1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"gpt-4o\",\"choices\":[]}\n\n" +
            "data: [DONE]\n\n");

        using var http = new HttpClient(bodyCapture) { BaseAddress = new Uri("https://test.invalid/") };
        using var client = new ShashuClient(
            new ShashuConfig { BaseUrl = TestGateway.Uri, ApiKey = "sk-trustgate-test" }, http);

        await foreach (var _ in client.Chat.Completions.CreateStreamAsync(
            new ChatCompletionRequest
            {
                Model = "gpt-4o",
                Messages = new[] { new ChatMessage("user", "hi") },
                // Stream omitted → null → must be forced to true.
            }))
        { }

        Assert.NotNull(bodyCapture.LastRequestBody);
        Assert.Contains("\"stream\":true", bodyCapture.LastRequestBody);
        Assert.Equal("text/event-stream", bodyCapture.LastAcceptHeader);
    }

    [Fact]
    public async Task Stream_5xx_throws_ShashuException_with_status_and_body()
    {
        using var client = BuildClient(
            sseBody: "{\"error\":{\"message\":\"upstream provider unavailable\"}}",
            status: HttpStatusCode.BadGateway);

        var ex = await Assert.ThrowsAsync<ShashuException>(async () =>
        {
            await foreach (var _ in client.Chat.Completions.CreateStreamAsync(
                new ChatCompletionRequest
                {
                    Model = "gpt-4o",
                    Messages = new[] { new ChatMessage("user", "hi") },
                    Stream = true,
                }))
            { }
        });

        Assert.Equal(502, ex.StatusCode);
        Assert.Contains("upstream provider unavailable", ex.ResponseBody);
    }

    [Fact]
    public async Task Stream_handles_data_with_or_without_space_after_colon()
    {
        // SSE spec: the field/value separator is `:` followed by an
        // OPTIONAL single space. Both should be parsed identically.
        var sse =
            "data: {\"id\":\"c1\",\"object\":\"chunk\",\"created\":1,\"model\":\"gpt-4o\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"with-space\"}}]}\n\n" +
            "data:{\"id\":\"c2\",\"object\":\"chunk\",\"created\":1,\"model\":\"gpt-4o\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"no-space\"}}]}\n\n" +
            "data: [DONE]\n\n";

        using var client = BuildClient(sseBody: sse, status: HttpStatusCode.OK);

        var contents = new List<string>();
        await foreach (var chunk in client.Chat.Completions.CreateStreamAsync(
            new ChatCompletionRequest
            {
                Model = "gpt-4o",
                Messages = new[] { new ChatMessage("user", "hi") },
                Stream = true,
            }))
        {
            if (chunk.Choices.Count > 0 && chunk.Choices[0].Delta.Content is { } c)
                contents.Add(c);
        }

        Assert.Equal(new[] { "with-space", "no-space" }, contents);
    }

    [Fact]
    public async Task CreateAsync_with_stream_true_throws_routing_caller_to_streaming_method()
    {
        // The SYNC method must guard against accidental streaming use —
        // otherwise the customer would silently miss SSE chunks.
        using var client = BuildClient(sseBody: "", status: HttpStatusCode.OK);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.Chat.Completions.CreateAsync(new ChatCompletionRequest
            {
                Model = "gpt-4o",
                Messages = new[] { new ChatMessage("user", "hi") },
                Stream = true,  // wrong method
            }));
        Assert.Contains("CreateStreamAsync", ex.Message);
    }

    // ------------------------------------------------------------------

    private static ShashuClient BuildClient(string sseBody, HttpStatusCode status)
    {
        var handler = new SseStubHandler(sseBody, status);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://test.invalid/") };
        return new ShashuClient(new ShashuConfig { BaseUrl = TestGateway.Uri, ApiKey = "sk-trustgate-test" }, http);
    }
}

/// <summary>Minimal SSE-emitting stub — returns the body verbatim.</summary>
internal sealed class SseStubHandler : HttpMessageHandler
{
    private readonly string _body;
    private readonly HttpStatusCode _status;

    public SseStubHandler(string body, HttpStatusCode status)
    {
        _body = body;
        _status = status;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = new HttpResponseMessage(_status)
        {
            Content = new StringContent(_body, Encoding.UTF8, "text/event-stream"),
        };
        return Task.FromResult(response);
    }
}

/// <summary>Captures the request body + Accept header for assertion.</summary>
internal sealed class BodyCapturingHandler : HttpMessageHandler
{
    private readonly string _body;
    public string? LastRequestBody { get; private set; }
    public string? LastAcceptHeader { get; private set; }

    public BodyCapturingHandler(string body) => _body = body;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Content is not null)
            LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        LastAcceptHeader = request.Headers.Accept.FirstOrDefault()?.MediaType;

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(_body, Encoding.UTF8, "text/event-stream"),
        };
    }
}
