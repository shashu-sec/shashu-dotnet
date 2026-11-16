using System.Collections.Immutable;

namespace Shashu.Internal;

/// <summary>
/// The ambient retrieval bus: what was retrieved, waiting for the model call.
/// </summary>
/// <remarks>
/// <para>A customer calls <c>Retrieval.DeclareRetrieval</c> where their
/// retrieval happens — in a repository method, a tool, a graph node — and the
/// SDK carries that declaration to the gateway on the next model call, wherever
/// that call is made from. This class is the carrier, and it is deliberately
/// the smallest thing that can be: an <see cref="AsyncLocal{T}"/> holding a
/// mutable <see cref="BusState"/> whose events are an immutable list.</para>
///
/// <para><b>Why a mutable holder around an immutable list, and not one or the
/// other.</b> <see cref="AsyncLocal{T}"/> is copy-on-write DOWN the async flow,
/// and the two halves of that sentence pull in opposite directions. Measured on
/// .NET 10; <c>RetrievalBusPropagationTests</c> re-asserts every row:</para>
///
/// <list type="table">
///   <listheader><term>write</term><description>who sees it</description></listheader>
///   <item><term>rebind <c>Value</c> in an async child</term>
///     <description>the child and below — NOT the parent, not siblings</description></item>
///   <item><term>rebind <c>Value</c> in a synchronous child</term>
///     <description>the caller too — there is no execution-context boundary</description></item>
///   <item><term>mutate a holder seeded by an ancestor</term>
///     <description>the ancestor, and everything else sharing that holder</description></item>
/// </list>
///
/// <para>So a rebind isolates siblings but cannot climb out of an async helper,
/// and an in-place mutation climbs out but is shared by every task under the
/// frame that seeded it — measured handing eight concurrent tasks each other's
/// documents when the holder was seeded above the fan-out. Neither carrier
/// alone is correct, which is why the bus is both:</para>
///
/// <list type="bullet">
///   <item><description><b>Default, no scope.</b> The first
///   <c>DeclareRetrieval</c> in a flow seeds a holder. Because that call is a
///   plain synchronous static method, the seed lands in the CALLER's execution
///   context, so the ordinary shape — retrieve, declare, call the model, all in
///   one async method — works with nothing extra. Concurrent tasks each seed
///   their own, so they are isolated.</description></item>
///   <item><description><b><c>RetrievalScope</c>.</b> Seeds the holder
///   explicitly at the top of a unit of work. A declaration made anywhere
///   inside — including inside an awaited helper that a rebind could never
///   climb out of — mutates that holder in place and is therefore visible at
///   the model call. This is what the frozen contract means by "if the
///   retrieval and the model call might not be in the same flow, wrap them in a
///   scope".</description></item>
/// </list>
///
/// <para><b>What this class will not do.</b> No disk, no network, no logging of
/// document text — not at DEBUG, not in an error path. The only place a preview
/// ever goes is the body of the request to the customer's own gateway.</para>
///
/// <para>Caps are the contract's, not the gateway's current behaviour: previews
/// are bound at 2048 characters (<c>rag_context.v1</c>'s <c>maxLength</c>)
/// rather than the 600 the gateway truncates at today, because that mismatch is
/// a gateway-side defect tracked separately and the SDK speaks the
/// contract.</para>
///
/// <para>Sibling of the Python SDK's <c>trustgate/_bus.py</c> and the Node
/// SDK's <c>src/_bus.ts</c>.</para>
/// </remarks>
internal static class RetrievalBus
{
    /// <summary>
    /// At most this many separate declarations are carried at once. A caller
    /// that retrieves in a loop would otherwise grow the bus without bound.
    /// </summary>
    internal const int MaxEvents = 8;

    /// <summary>
    /// <c>rag_context.v1</c> <c>docs.maxItems</c>. Counted across ALL carried
    /// events, not per event, because the joiner emits one <c>docs</c> array.
    /// </summary>
    internal const int MaxDocs = 64;

    /// <summary><c>rag_context.v1</c> <c>docs.items.properties.preview.maxLength</c>.</summary>
    internal const int MaxPreviewChars = 2048;

    /// <summary><c>rag_context.v1</c> <c>corpus.maxLength</c>.</summary>
    internal const int MaxCorpusChars = 256;

    /// <summary><c>rag_context.v1</c> <c>query.maxLength</c>.</summary>
    internal const int MaxQueryChars = 4096;

    /// <summary>
    /// The mutable holder. Mutating one of these in place is what lets a
    /// declaration made inside an awaited helper reach the model call in the
    /// frame that opened the scope.
    /// </summary>
    internal sealed class BusState
    {
        private ImmutableList<RetrievalEvent> _events = ImmutableList<RetrievalEvent>.Empty;
        private ImmutableList<MemoryEvent> _memory = ImmutableList<MemoryEvent>.Empty;

        // Not about async concurrency — about a customer who fans out inside a
        // single RetrievalScope, where several tasks legitimately share one
        // holder and would otherwise tear the list.
        private readonly object _gate = new();

        internal ImmutableList<RetrievalEvent> Events
        {
            get { lock (_gate) { return _events; } }
        }

        internal ImmutableList<MemoryEvent> MemoryEvents
        {
            get { lock (_gate) { return _memory; } }
        }

        internal void Add(RetrievalEvent evt)
        {
            lock (_gate) { _events = Trim(_events.Add(evt)); }
        }

        internal void AddMemory(MemoryEvent evt)
        {
            lock (_gate) { _memory = TrimMemory(_memory.Add(evt)); }
        }

        internal ImmutableList<RetrievalEvent> TakeAll()
        {
            lock (_gate)
            {
                var taken = _events;
                _events = ImmutableList<RetrievalEvent>.Empty;
                return taken;
            }
        }

        internal ImmutableList<MemoryEvent> TakeMemory()
        {
            lock (_gate)
            {
                var taken = _memory;
                _memory = ImmutableList<MemoryEvent>.Empty;
                return taken;
            }
        }

        internal void Reset()
        {
            lock (_gate)
            {
                _events = ImmutableList<RetrievalEvent>.Empty;
                _memory = ImmutableList<MemoryEvent>.Empty;
            }
        }

        internal void ResetRetrieval()
        {
            lock (_gate) { _events = ImmutableList<RetrievalEvent>.Empty; }
        }

        internal void ResetMemory()
        {
            lock (_gate) { _memory = ImmutableList<MemoryEvent>.Empty; }
        }
    }

    private static readonly AsyncLocal<BusState?> Current = new();

    /// <summary>
    /// Cheap emptiness check — the joiner's zero-cost guard. One
    /// <see cref="AsyncLocal{T}"/> read and a count, touching no request, no
    /// header and no body, which is what makes the no-retrieval path free.
    /// </summary>
    internal static bool HasEvents() =>
        Current.Value is { } state && (!state.Events.IsEmpty || !state.MemoryEvents.IsEmpty);

    /// <summary>
    /// Append one declaration to the flow's holder, seeding one if this is the
    /// first declaration here. A declaration with no readable documents is
    /// dropped rather than carried — <c>docs</c> has <c>minItems: 1</c>.
    /// </summary>
    internal static void Push(RetrievalEvent evt)
    {
        if (evt.Docs.Count == 0) return;
        Ensure().Add(evt);
    }

    internal static void PushMemory(MemoryEvent evt)
    {
        if (evt.Items.Count == 0) return;
        Ensure().AddMemory(evt);
    }

    /// <summary>Everything carried right now, already trimmed to the caps.</summary>
    internal static ImmutableList<RetrievalEvent> Snapshot() =>
        Current.Value?.Events ?? ImmutableList<RetrievalEvent>.Empty;

    /// <summary>
    /// Take everything carried and empty the bus in one step. The joiner uses
    /// this so retrieval declared for request N can never reach request N+1,
    /// and so a concurrent declaration cannot be lost between a read and a
    /// clear.
    /// </summary>
    internal static ImmutableList<RetrievalEvent> TakeAll() =>
        Current.Value?.TakeAll() ?? ImmutableList<RetrievalEvent>.Empty;

    internal static ImmutableList<MemoryEvent> TakeMemory() =>
        Current.Value?.TakeMemory() ?? ImmutableList<MemoryEvent>.Empty;

    /// <summary>Drop everything carried, leaving the holder itself in place.</summary>
    internal static void Clear() => Current.Value?.Reset();

    internal static void ClearRetrieval() => Current.Value?.ResetRetrieval();

    internal static void ClearMemory() => Current.Value?.ResetMemory();

    /// <summary>
    /// Seed a fresh holder for a unit of work and return the previous binding
    /// so <c>RetrievalScope</c> can put it back on dispose.
    /// </summary>
    internal static BusState? Enter()
    {
        var previous = Current.Value;
        Current.Value = new BusState();
        return previous;
    }

    /// <summary>Restore a binding captured by <see cref="Enter"/>.</summary>
    internal static void Exit(BusState? previous) => Current.Value = previous;

    /// <summary>The flow's holder, seeding one if absent.</summary>
    /// <remarks>
    /// The seed is a rebind, so it reaches the caller only because
    /// <c>DeclareRetrieval</c> is synchronous (see the table in the class
    /// remarks). A declaration made inside an awaited helper with no enclosing
    /// scope seeds a holder that dies with that helper — the documented
    /// boundary <c>RetrievalScope</c> exists to cross, not a defect to paper
    /// over with a process-wide holder that would leak every concurrent
    /// request's documents into every other.
    /// </remarks>
    private static BusState Ensure() => Current.Value ??= new BusState();

    /// <summary>
    /// Enforce <see cref="MaxEvents"/> then <see cref="MaxDocs"/>, keeping the
    /// newest of each.
    /// </summary>
    /// <remarks>
    /// Trimming keeps the MOST RECENT and drops the oldest, never the reverse:
    /// the declaration nearest the model call is the one that describes the
    /// prompt being sent. Applied uniformly, that means an oversized single
    /// declaration keeps its tail — 100 documents in one call are carried as
    /// the last 64.
    /// </remarks>
    private static ImmutableList<RetrievalEvent> Trim(ImmutableList<RetrievalEvent> events)
    {
        if (events.Count > MaxEvents)
            events = events.GetRange(events.Count - MaxEvents, MaxEvents);

        var budget = MaxDocs;
        var kept = new List<RetrievalEvent>(events.Count);
        for (var i = events.Count - 1; i >= 0 && budget > 0; i--)
        {
            var evt = events[i];
            if (evt.Docs.Count > budget)
            {
                kept.Add(evt with { Docs = evt.Docs.GetRange(evt.Docs.Count - budget, budget) });
                budget = 0;
            }
            else
            {
                kept.Add(evt);
                budget -= evt.Docs.Count;
            }
        }
        kept.Reverse();
        return ImmutableList.CreateRange(kept);
    }

    private static ImmutableList<MemoryEvent> TrimMemory(ImmutableList<MemoryEvent> events)
    {
        if (events.Count > MaxEvents)
            events = events.GetRange(events.Count - MaxEvents, MaxEvents);

        var budget = MaxDocs;
        var kept = new List<MemoryEvent>(events.Count);
        for (var i = events.Count - 1; i >= 0 && budget > 0; i--)
        {
            var evt = events[i];
            if (evt.Items.Count > budget)
            {
                kept.Add(evt with { Items = evt.Items.GetRange(evt.Items.Count - budget, budget) });
                budget = 0;
            }
            else
            {
                kept.Add(evt);
                budget -= evt.Items.Count;
            }
        }
        kept.Reverse();
        return ImmutableList.CreateRange(kept);
    }
}

/// <summary>One retrieval declaration, already coerced to the wire shape.</summary>
internal sealed record RetrievalEvent(
    string Corpus,
    string? Query,
    string Source,
    ImmutableList<RagContextDoc> Docs);

/// <summary>
/// One document as <c>rag_context.v1</c> <c>docs[]</c> spells it. Optional
/// fields are null when absent, because <c>additionalProperties: false</c>
/// makes a malformed one reject the whole payload.
/// </summary>
internal sealed record RagContextDoc(
    string Id,
    string Preview,
    double? Score,
    string? SourceUri,
    string? Hash);
