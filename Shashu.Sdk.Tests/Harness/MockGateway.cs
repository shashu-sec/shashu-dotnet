using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Shashu.Tests.Harness;

/// <summary>
/// The gateway address every test uses. RFC 6761 reserves <c>.invalid</c> and
/// guarantees it never resolves, so a test that reached the network by mistake
/// fails on DNS rather than quietly succeeding against something real.
/// </summary>
internal static class TestGateway
{
    internal static readonly Uri Uri = new("https://gateway.test.invalid/");
}

/// <summary>
/// An in-repo mock gateway: records every request verbatim and returns a canned
/// completion. No socket is ever opened.
/// </summary>
/// <remarks>
/// <para>Requests are captured as the fully-materialised bytes the handler was
/// about to send, so a test can assert on the exact wire body — including
/// asserting that two runs are byte-identical, which is how the zero-cost claim
/// is proved rather than described.</para>
/// <para>Thread-safe: the concurrency case drives eight tasks through one
/// instance at once.</para>
/// </remarks>
internal sealed class MockGateway : HttpMessageHandler
{
    private readonly ConcurrentQueue<RecordedRequest> _requests = new();

    /// <summary>Every request that reached the gateway, in arrival order.</summary>
    internal IReadOnlyList<RecordedRequest> Requests => _requests.ToArray();

    /// <summary>The single request, asserting there was exactly one.</summary>
    internal RecordedRequest Single()
    {
        var all = Requests;
        if (all.Count != 1)
            throw new InvalidOperationException($"expected exactly 1 request, recorded {all.Count}");
        return all[0];
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null
            ? Array.Empty<byte>()
            : await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

        _requests.Enqueue(new RecordedRequest(
            request.RequestUri,
            body,
            request.Content?.Headers.ContentLength,
            request.Content?.Headers.ContentType?.ToString()));

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(CannedCompletion, Encoding.UTF8, "application/json"),
        };
    }

    private const string CannedCompletion = """
        {
          "id": "chatcmpl-mock",
          "object": "chat.completion",
          "created": 0,
          "model": "gpt-4o-mini",
          "choices": [
            {
              "index": 0,
              "message": { "role": "assistant", "content": "ok" },
              "finish_reason": "stop"
            }
          ],
          "usage": { "prompt_tokens": 1, "completion_tokens": 1, "total_tokens": 2 }
        }
        """;
}

/// <summary>One request as the gateway saw it.</summary>
internal sealed record RecordedRequest(
    Uri? RequestUri,
    byte[] Body,
    long? ContentLength,
    string? ContentType)
{
    /// <summary>The body parsed as a JSON object, or null if it was not one.</summary>
    internal JsonObject? Json =>
        Body.Length == 0 ? null : JsonNode.Parse(Body) as JsonObject;

    /// <summary>
    /// The <c>shashu_metadata.rag_context</c> block, or null if the request
    /// carried none. The negative case asserts on this being null.
    /// </summary>
    internal JsonObject? RagContext =>
        Json?["shashu_metadata"] is JsonObject metadata
            ? metadata["rag_context"] as JsonObject
            : null;

    internal JsonObject? MemoryContext =>
        Json?["shashu_metadata"] is JsonObject metadata
            ? metadata["memory_context"] as JsonObject
            : null;

    /// <summary>Whether the request carried a <c>shashu_metadata</c> key at all.</summary>
    internal bool HasShashuMetadata => Json?["shashu_metadata"] is not null;
}
