using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Shashu;
using Shashu.Chat;
using Shashu.Internal;
using Shashu.Tests.Harness;
using Xunit;

namespace Shashu.Tests;

/// <summary>
/// A retrieval declaration belongs to the one call it was made for, whatever
/// the body size: it is attached to that call and never carried over to
/// another.
/// </summary>
public class RetrievalPerCallTests
{
    /// <summary>Above the 8 MiB cap the joiner used to apply.</summary>
    private const int LargeBytes = 9 * 1024 * 1024;

    private sealed class Capture : HttpMessageHandler
    {
        public ConcurrentQueue<string> Bodies { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? ""
                : Encoding.UTF8.GetString(await request.Content.ReadAsByteArrayAsync(cancellationToken));
            Bodies.Enqueue(body);
            if (body.Contains("CANCEL-ME"))
                await Task.Delay(Timeout.Infinite, cancellationToken);
            if (body.Contains("FAIL-THIS-CALL"))
            {
                return new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent("{\"error\":{\"message\":\"bad\",\"code\":\"bad_request\"}}"),
                };
            }
            if (request.Headers.Accept.Any(a => a.MediaType == "text/event-stream"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "data: {\"id\":\"x\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"ok\"}}]}\n\ndata: [DONE]\n\n",
                        Encoding.UTF8,
                        "text/event-stream"),
                };
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"id\":\"x\",\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}",
                    Encoding.UTF8,
                    "application/json"),
            };
        }

        public string Find(string marker) =>
            Bodies.FirstOrDefault(b => b.Contains(marker))
            ?? throw new Xunit.Sdk.XunitException($"no request carried {marker}");
    }

    [Fact]
    public async Task A_large_request_carries_its_declaration()
    {
        RetrievalBus.Clear();
        var capture = new Capture();
        using var client = Client(capture, out var http);
        using (http)
        {
            Retrieval.DeclareRetrieval(new[] { "large declared document" }, corpus: "large_corpus", query: "q");
            await client.Chat.Completions.CreateAsync(Large());
        }
        var sent = Assert.Single(capture.Bodies);
        Assert.True(sent.Length > LargeBytes);
        Assert.True(sent.Contains("large_corpus"), "the large request lost its declaration");
    }

    [Fact]
    public async Task B_the_next_undeclared_call_carries_nothing()
    {
        RetrievalBus.Clear();
        var capture = new Capture();
        using var client = Client(capture, out var http);
        using (http)
        {
            Retrieval.DeclareRetrieval(new[] { "large declared document" }, corpus: "large_corpus", query: "q");
            await client.Chat.Completions.CreateAsync(Large());
            await client.Chat.Completions.CreateAsync(Small("next"));
        }
        Assert.Equal(2, capture.Bodies.Count);
        var next = capture.Find("\"next\"");
        Assert.False(next.Contains("shashu_metadata"), "leftover leaked: " + next);
    }

    [Fact]
    public async Task C_concurrent_large_and_small_each_carry_only_their_own()
    {
        RetrievalBus.Clear();
        var capture = new Capture();
        using var client = Client(capture, out var http);
        using (http)
        {
            var declared = new CountdownEvent(2);
            async Task Run(string corpus, ChatCompletionRequest first, string after)
            {
                Retrieval.DeclareRetrieval(new[] { corpus + " declared document" }, corpus: corpus, query: "q");
                declared.Signal();
                declared.Wait(TimeSpan.FromSeconds(30));
                await client.Chat.Completions.CreateAsync(first);
                // The same async flow then makes an undeclared call: nothing may be left over.
                await client.Chat.Completions.CreateAsync(Small(after));
            }
            await Task.WhenAll(
                Task.Run(() => Run("large_corpus", Large(), "after-large")),
                Task.Run(() => Run("small_corpus", Small("small"), "after-small")));
        }
        var large = capture.Find("LARGE-MARKER");
        var small = capture.Find("\"small\"");
        Assert.True(large.Contains("large_corpus"), "large lost its own declaration");
        Assert.DoesNotContain("small_corpus", large);
        Assert.Contains("small_corpus", small);
        Assert.DoesNotContain("large_corpus", small);
        Assert.False(capture.Find("after-large").Contains("shashu_metadata"), "leftover leaked");
        Assert.False(capture.Find("after-small").Contains("shashu_metadata"), "leftover leaked");
    }

    [Fact]
    public async Task D_a_streamed_call_carries_its_declaration_and_leaves_nothing()
    {
        RetrievalBus.Clear();
        var capture = new Capture();
        using var client = Client(capture, out var http);
        using (http)
        {
            Retrieval.DeclareRetrieval(new[] { "stream declared document" }, corpus: "stream_corpus", query: "q");
            await foreach (var _ in client.Chat.Completions.CreateStreamAsync(Small("stream")))
            {
            }
            await client.Chat.Completions.CreateAsync(Small("next"));
        }
        Assert.True(capture.Find("\"stream\"").Contains("stream_corpus"), "the stream lost its declaration");
        var next = capture.Find("\"next\"");
        Assert.False(next.Contains("shashu_metadata"), "leftover leaked: " + next);
    }

    [Fact]
    public async Task E_a_failed_call_leaves_nothing_for_the_next()
    {
        RetrievalBus.Clear();
        var capture = new Capture();
        using var client = Client(capture, out var http);
        using (http)
        {
            Retrieval.DeclareRetrieval(new[] { "failed declared document" }, corpus: "failed_corpus", query: "q");
            await Assert.ThrowsAnyAsync<ShashuException>(
                () => client.Chat.Completions.CreateAsync(Small("FAIL-THIS-CALL")));
            await client.Chat.Completions.CreateAsync(Small("next"));
        }
        Assert.Contains("failed_corpus", capture.Find("FAIL-THIS-CALL"));
        var next = capture.Find("\"next\"");
        Assert.False(next.Contains("shashu_metadata"), "leftover leaked: " + next);
    }

    [Fact]
    public async Task F_a_cancelled_call_leaves_nothing_for_the_next()
    {
        RetrievalBus.Clear();
        var capture = new Capture();
        using var client = Client(capture, out var http);
        using (http)
        {
            Retrieval.DeclareRetrieval(new[] { "cancelled declared document" }, corpus: "cancelled_corpus", query: "q");
            // Cancelled while the request is in flight.
            using var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => client.Chat.Completions.CreateAsync(Small("CANCEL-ME"), cancelled.Token));
            await client.Chat.Completions.CreateAsync(Small("next"));
        }
        var next = capture.Find("\"next\"");
        Assert.False(next.Contains("shashu_metadata"), "leftover leaked: " + next);
    }

    private static ShashuClient Client(Capture capture, out HttpClient http)
    {
        http = new HttpClient(capture) { BaseAddress = new Uri("https://test.invalid/") };
        return new ShashuClient(
            new ShashuConfig { BaseUrl = TestGateway.Uri, ApiKey = "shashu_sk_percall", MaxRetries = 0 }, http);
    }

    private static ChatCompletionRequest Small(string text) =>
        new() { Model = "m", Messages = [new ChatMessage("user", text)] };

    private static ChatCompletionRequest Large()
    {
        var pdf = new byte[LargeBytes];
        new Random(7).NextBytes(pdf);
        return new ChatCompletionRequest
        {
            Model = "m",
            Messages =
            [
                ChatMessage.WithParts("user", [ContentPart.Text("LARGE-MARKER"), ContentPart.File(pdf, "large.pdf", "application/pdf")]),
            ],
        };
    }
}
