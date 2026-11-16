using Shashu.Configuration;
using Shashu.Internal;

namespace Shashu;

/// <summary>
/// The memory declaration surface: tell Shashu AI which memory items were
/// bound into the prompt. Same unit-of-work shape as <see cref="Retrieval"/>.
/// </summary>
/// <remarks>
/// There are exactly two members here. Collectors are not shipped in this
/// release. Both members are no-ops when <c>SHASHU_DISABLE=1</c>.
/// </remarks>
public static class Memory
{
    /// <summary>
    /// Declare memory items for the next model call on this async context.
    /// </summary>
    public static void DeclareMemory(
        object? items,
        string store,
        string scope = "agent",
        string source = "sdk")
    {
        if (ShashuEnv.IsDisabled()) return;

        var coerced = RagContextBuilder.CoerceDocs(items);
        if (coerced.Count == 0) return;

        RetrievalBus.PushMemory(new MemoryEvent(
            Store: RequireStore(store),
            Scope: MemoryContextBuilder.RequireScope(scope),
            Source: MemoryContextBuilder.RequireSource(source),
            Items: coerced));
    }

    /// <summary>
    /// Same as <see cref="DeclareMemory"/>, restored when the returned scope is disposed.
    /// </summary>
    public static IDisposable MemoryScope(
        object? items,
        string store,
        string scope = "agent",
        string source = "sdk")
    {
        if (ShashuEnv.IsDisabled()) return InertScope.Instance;

        var coerced = RagContextBuilder.CoerceDocs(items);
        var previous = RetrievalBus.Enter();
        if (coerced.Count > 0)
        {
            RetrievalBus.PushMemory(new MemoryEvent(
                Store: RequireStore(store),
                Scope: MemoryContextBuilder.RequireScope(scope),
                Source: MemoryContextBuilder.RequireSource(source),
                Items: coerced));
        }
        return new Scope(previous);
    }

    private static string RequireStore(string store)
    {
        if (string.IsNullOrWhiteSpace(store))
        {
            throw new ArgumentException(
                "DeclareMemory needs a non-empty store: it is what the gateway "
                + "records the items as having come from, and memory_context.v1 "
                + "requires it.",
                nameof(store));
        }
        var trimmed = store.Trim();
        return trimmed.Length > RetrievalBus.MaxCorpusChars
            ? trimmed[..RetrievalBus.MaxCorpusChars]
            : trimmed;
    }

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

    private sealed class InertScope : IDisposable
    {
        internal static readonly InertScope Instance = new();
        public void Dispose() { }
    }
}
