using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Shashu.Configuration;

namespace Shashu.Internal;

/// <summary>
/// Joins the carried retrieval declaration onto the outgoing request.
/// </summary>
/// <remarks>
/// <para>Four properties it has to hold, in this order:</para>
/// <list type="number">
///   <item><description><b>Zero cost when nothing was retrieved.</b> The first
///   thing it does is a single <see cref="AsyncLocal{T}"/> read. With an empty
///   bus it returns before touching the request's headers or body at all, so
///   the bytes on the wire are the bytes the unpatched handler produced —
///   asserted byte-for-byte in the harness.</description></item>
///   <item><description><b>Explicit always wins.</b> A body already carrying
///   <c>trustgate_metadata.rag_context</c> is left exactly as the caller built
///   it. The ambient declaration is consumed and discarded, not deferred: a
///   declaration that lost this call must not turn up on the next
///   one.</description></item>
///   <item><description><b>Snapshot and clear.</b> The bus is emptied the
///   moment it is written to a request. Retrieval declared for request N never
///   reaches request N+1.</description></item>
///   <item><description><b>Never corrupt a body it does not understand.</b> A
///   non-JSON body, a non-object JSON body, or a streaming upload is passed
///   through untouched and does NOT consume the bus — the next real model call
///   still carries the declaration, and a multi-gigabyte file upload does not
///   eat it.</description></item>
/// </list>
/// <para>Sibling of the Python SDK's <c>trustgate/_joiner.py</c> and the Node
/// SDK's <c>src/_joiner.ts</c>.</para>
/// </remarks>
internal static class RetrievalJoiner
{
    /// <summary>
    /// A body larger than this is passed through untouched. Buffering an
    /// arbitrary upload to look for a JSON object is the kind of hidden cost an
    /// instrumentation layer must not impose; a chat completion is orders of
    /// magnitude below it.
    /// </summary>
    private const long MaxBodyBytes = 8 * 1024 * 1024;

    /// <summary>Attach the carried declaration to <paramref name="request"/>, in place.</summary>
    /// <remarks>Never throws: a retrieval declaration must not be the thing that
    /// breaks a customer's request.</remarks>
    internal static async Task JoinAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // The zero-cost guard: one AsyncLocal read, before anything is touched.
        if (!RetrievalBus.HasEvents()) return;

        // Checked second, so the kill switch costs nothing on the common path.
        // A bus populated before the switch flipped is dropped, not sent.
        if (ShashuEnv.IsDisabled())
        {
            RetrievalBus.Clear();
            return;
        }

        try
        {
            await JoinCoreAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The declaration is lost, the request goes out as the customer
            // built it. Nothing about the document text is logged.
            Console.Error.WriteLine(
                $"[trustgate.retrieval] declaration not attached: {ex.GetType().Name}");
        }
    }

    private static async Task JoinCoreAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Content is not { } content) return;
        if (!LooksLikeJson(content.Headers.ContentType)) return;
        if (content.Headers.ContentLength is > MaxBodyBytes) return;

        var raw = await content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        if (raw.Length == 0 || raw.Length > MaxBodyBytes) return;

        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(raw);
        }
        catch (JsonException)
        {
            return;   // not JSON after all — pass through, bus untouched
        }
        if (parsed is not JsonObject body) return;

        var existing = body["trustgate_metadata"] as JsonObject;
        var explicitRag = existing?["rag_context"] is not null;
        var explicitMem = existing?["memory_context"] is not null;
        if (explicitRag) RetrievalBus.ClearRetrieval();
        if (explicitMem) RetrievalBus.ClearMemory();

        var context = explicitRag ? null : RagContextBuilder.Build(RetrievalBus.TakeAll());
        var memoryContext = explicitMem ? null : MemoryContextBuilder.Build(RetrievalBus.TakeMemory());
        if (context is null && memoryContext is null) return;

        if (body["trustgate_metadata"] is not JsonObject metadata)
        {
            metadata = new JsonObject();
            body["trustgate_metadata"] = metadata;
        }
        if (context is not null)
            metadata["rag_context"] = JsonSerializer.SerializeToNode(context);
        if (memoryContext is not null)
            metadata["memory_context"] = memoryContext;

        var encoded = Encoding.UTF8.GetBytes(body.ToJsonString());
        var replacement = new ByteArrayContent(encoded);
        foreach (var header in content.Headers)
        {
            if (string.Equals(header.Key, "Content-Length", StringComparison.OrdinalIgnoreCase))
                continue;   // set from the new length below, never copied
            replacement.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        replacement.Headers.ContentLength = encoded.LongLength;

        request.Content = replacement;
        content.Dispose();
    }

    /// <summary>
    /// Whether the body is worth parsing. A missing content type is treated as
    /// JSON because vendor SDKs do send one, and the parse below is the real
    /// gate either way.
    /// </summary>
    private static bool LooksLikeJson(MediaTypeHeaderValue? contentType)
    {
        var mediaType = contentType?.MediaType;
        if (string.IsNullOrEmpty(mediaType)) return true;
        return mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase)
            || mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase);
    }
}
