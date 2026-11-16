using Shashu.Configuration;
using Shashu.Internal;

namespace Shashu;

/// <summary>
/// The retrieval declaration surface: tell Shashu what was bound into the
/// prompt, wherever that happened.
/// </summary>
/// <remarks>
/// <para>There are exactly two members here and there is no third, per
/// <c>contracts/sdk-surface.v1.md</c>. Both are no-ops when
/// <c>TRUSTGATE_DISABLE=1</c>.</para>
///
/// <para><b>Calling it.</b> The type is a holder — C# has no free functions,
/// so the two entry points need one. It is named for what it does rather than
/// for the product, so the qualifier reads the same either way you write it:</para>
/// <code>
/// using static Shashu.Retrieval;   // then: DeclareRetrieval(docs, corpus: "policy_docs_v2");
/// // or fully qualified:
/// Shashu.Retrieval.DeclareRetrieval(docs, corpus: "policy_docs_v2");
/// </code>
/// <para>The member names, their order and their defaults are exactly the
/// frozen ones.</para>
///
/// <para><b>Which form to use.</b> <see cref="DeclareRetrieval"/> is enough
/// whenever the retrieval and the model call are in the same async method —
/// the common shape. Reach for <see cref="RetrievalScope"/> when they might not
/// be: the declaration then binds to the unit of work rather than to the frame
/// that made it. <see cref="Internal.RetrievalBus"/> documents exactly what
/// <see cref="AsyncLocal{T}"/> propagates, and why one carrier could not do
/// both jobs.</para>
///
/// <para>Nothing is sent until a model call happens. A declaration that no
/// model call follows is discarded when its flow ends — it never reaches the
/// network, and it never attaches itself to a later, unrelated request.</para>
/// </remarks>
public static class Retrieval
{
    /// <summary>
    /// Declare the documents bound into the prompt, wherever the retrieval
    /// happened.
    /// </summary>
    /// <remarks>
    /// <para>The declaration rides an <see cref="AsyncLocal{T}"/> and is
    /// attached to the NEXT outgoing model call from this flow, by whichever
    /// <see cref="Patching.ShashuRoutingHandler"/> sends it — you do not
    /// have to be anywhere near that call.</para>
    /// <code>
    /// var hits = await _vectorStore.SearchAsync(question, k: 5);
    /// DeclareRetrieval(hits, corpus: "policy_docs_v2", query: question);
    /// // ... any amount of unrelated code ...
    /// var answer = await _openAi.GetChatCompletionsAsync(...);   // attached here
    /// </code>
    /// <para>Called from an awaited helper with no enclosing
    /// <see cref="RetrievalScope"/>, the declaration cannot climb back out to
    /// the caller — that is what <see cref="AsyncLocal{T}"/> does, and the
    /// scope is how you cross it.</para>
    /// </remarks>
    /// <param name="docs">
    /// The documents bound into the prompt — plain strings, dictionaries, or
    /// any object whose properties spell the usual field names (<c>Text</c>,
    /// <c>Content</c>, <c>PageContent</c>, <c>Preview</c>, plus <c>Id</c>,
    /// <c>Score</c>, <c>SourceUri</c>, <c>Hash</c> and a <c>Metadata</c> bag).
    /// A single document may be passed unwrapped. Anything the table cannot
    /// read text out of is dropped rather than stringified.
    /// </param>
    /// <param name="corpus">
    /// The index the documents came from. Required by the contract — it is what
    /// the gateway records the documents as having come from.
    /// </param>
    /// <param name="query">The retrieval query, for the gateway's audit record.</param>
    /// <param name="source">
    /// What produced this declaration; a <c>rag_context.v1</c> enum value.
    /// </param>
    /// <exception cref="ArgumentException">
    /// A blank <paramref name="corpus"/>, or a <paramref name="source"/>
    /// outside the contract's enum. Both are call-site mistakes, and throwing
    /// here is the only place they can be seen — the alternative is a payload
    /// the gateway rejects at the far end of a customer's stack.
    /// </exception>
    public static void DeclareRetrieval(
        object? docs,
        string corpus,
        string? query = null,
        string source = "manual")
    {
        if (ShashuEnv.IsDisabled()) return;

        RetrievalBus.Push(new RetrievalEvent(
            Corpus: RequireCorpus(corpus),
            Query: TruncateQuery(query),
            Source: RequireSource(source),
            Docs: RagContextBuilder.CoerceDocs(docs)));
    }

    /// <summary>
    /// <see cref="DeclareRetrieval"/> bound to a unit of work rather than to
    /// the frame that called it.
    /// </summary>
    /// <remarks>
    /// <para>Model calls made inside the scope carry the declaration, and so do
    /// declarations made inside it by code the scope calls — including awaited
    /// helpers, which a bare <see cref="DeclareRetrieval"/> could not reach back
    /// out of. On dispose the bus returns to its prior state, so a retrieval
    /// that ended up not being used cannot attach itself to a later, unrelated
    /// call.</para>
    /// <code>
    /// using (RetrievalScope(hits, corpus: "policy_docs_v2", query: question))
    /// {
    ///     var answer = await _chain.InvokeAsync(question);
    /// }
    /// </code>
    /// <para><b>Do not fan out inside one scope</b> and expect isolation: tasks
    /// started under a scope share its holder by design, which is what lets a
    /// worker's declaration reach the caller. Concurrent requests should each
    /// open their own scope (or use the bare
    /// <see cref="DeclareRetrieval"/> path, which isolates them).</para>
    /// </remarks>
    /// <param name="docs">As <see cref="DeclareRetrieval"/>.</param>
    /// <param name="corpus">As <see cref="DeclareRetrieval"/>.</param>
    /// <param name="query">As <see cref="DeclareRetrieval"/>.</param>
    /// <param name="source">As <see cref="DeclareRetrieval"/>.</param>
    /// <returns>
    /// A handle whose disposal ends the scope. Disposing twice is harmless;
    /// never disposing it leaks nothing beyond the flow it was opened in.
    /// </returns>
    public static IDisposable RetrievalScope(
        object? docs,
        string corpus,
        string? query = null,
        string source = "manual")
    {
        if (ShashuEnv.IsDisabled()) return InertScope.Instance;

        // Validate BEFORE seeding, so a bad call site throws without leaving a
        // scope the customer's `using` would then have to unwind.
        var evt = new RetrievalEvent(
            Corpus: RequireCorpus(corpus),
            Query: TruncateQuery(query),
            Source: RequireSource(source),
            Docs: RagContextBuilder.CoerceDocs(docs));

        var previous = RetrievalBus.Enter();
        RetrievalBus.Push(evt);
        return new Scope(previous);
    }

    private static string RequireCorpus(string corpus)
    {
        if (string.IsNullOrWhiteSpace(corpus))
        {
            throw new ArgumentException(
                "DeclareRetrieval needs a non-empty corpus: it is what the gateway "
                + "records the documents as having come from, and rag_context.v1 "
                + "requires it.",
                nameof(corpus));
        }
        var trimmed = corpus.Trim();
        return trimmed.Length > RetrievalBus.MaxCorpusChars
            ? trimmed[..RetrievalBus.MaxCorpusChars]
            : trimmed;
    }

    private static string RequireSource(string source)
    {
        if (!RagContextBuilder.Sources.Contains(source))
        {
            throw new ArgumentException(
                $"DeclareRetrieval source='{source}' is not a rag_context.v1 value. "
                + $"Use one of: {string.Join(", ", RagContextBuilder.Sources)}. A new "
                + "integration adds its value to the contract first.",
                nameof(source));
        }
        return source;
    }

    private static string? TruncateQuery(string? query)
    {
        if (string.IsNullOrEmpty(query)) return null;
        return query.Length > RetrievalBus.MaxQueryChars
            ? query[..RetrievalBus.MaxQueryChars]
            : query;
    }

    /// <summary>Restores the bus binding the scope replaced.</summary>
    private sealed class Scope : IDisposable
    {
        private readonly RetrievalBus.BusState? _previous;
        private bool _disposed;

        internal Scope(RetrievalBus.BusState? previous) => _previous = previous;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            RetrievalBus.Exit(_previous);
        }
    }

    /// <summary>What <c>TRUSTGATE_DISABLE=1</c> hands back: a scope that does nothing.</summary>
    private sealed class InertScope : IDisposable
    {
        internal static readonly InertScope Instance = new();
        public void Dispose() { }
    }
}
