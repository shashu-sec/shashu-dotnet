using System.Collections.Concurrent;
using Shashu.Internal;
using Xunit;

namespace Shashu.Tests;

/// <summary>
/// What <see cref="AsyncLocal{T}"/> actually propagates, asserted rather than
/// assumed — the measurements the bus is designed around.
/// </summary>
/// <remarks>
/// <para>These are the rows in <c>RetrievalBus</c>'s remarks table. They are
/// here, in the suite, rather than in a one-off scratch program, because the
/// bus's whole design rests on them: if a future runtime changed one, the
/// carrier choice would be wrong and every other retrieval test would still
/// pass while customers silently lost declarations.</para>
/// <para>Two of these deliberately assert a NEGATIVE — a write that does NOT
/// reach the reader. That is not a defect being enshrined; it is the boundary
/// <c>RetrievalScope</c> exists to cross, and a test that stopped failing would
/// mean the boundary had moved.</para>
/// </remarks>
public class RetrievalBusPropagationTests
{
    private static readonly AsyncLocal<string?> Rebound = new();
    private static readonly AsyncLocal<List<string>?> Holder = new();

    // --- the raw runtime behaviour ----------------------------------------

    [Fact]
    public async Task Rebinding_inside_an_async_child_does_not_reach_the_parent()
    {
        static async Task Child() { await Task.Yield(); Rebound.Value = "child"; }

        Rebound.Value = null;
        await Child();

        Assert.Null(Rebound.Value);
    }

    [Fact]
    public void Rebinding_inside_a_synchronous_child_does_reach_the_caller()
    {
        // This is why DeclareRetrieval is a plain synchronous method: the holder
        // it seeds lands in the CALLER's context, so the ordinary
        // retrieve-declare-call shape needs no scope.
        static void Child() => Rebound.Value = "child";

        Rebound.Value = null;
        Child();

        Assert.Equal("child", Rebound.Value);
    }

    [Fact]
    public async Task Mutating_a_holder_seeded_by_the_parent_does_reach_the_parent()
    {
        static async Task Child() { await Task.Yield(); Holder.Value!.Add("child"); }

        Holder.Value = [];
        await Child();

        Assert.Contains("child", Holder.Value!);
    }

    [Fact]
    public async Task A_holder_shared_across_a_fan_out_is_shared_by_every_task()
    {
        // The hazard the bus avoids by NOT seeding a process-wide holder: eight
        // tasks under one holder see each other's writes.
        Holder.Value = [];
        await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(async () =>
        {
            await Task.Delay(1 + (i % 3));
            lock (Holder.Value!) { Holder.Value!.Add($"nonce-{i}"); }
        })));

        Assert.Equal(8, Holder.Value!.Count);
    }

    // --- the same questions, asked of the real bus -------------------------

    [Fact]
    public void Declaring_then_calling_in_one_flow_is_visible_without_a_scope()
    {
        RetrievalBus.Clear();
        Retrieval.DeclareRetrieval(new[] { "the ordinary shape" }, "corpus-a");

        Assert.True(RetrievalBus.HasEvents());
        RetrievalBus.Clear();
    }

    [Fact]
    public async Task Declaring_inside_an_awaited_helper_is_NOT_visible_without_a_scope()
    {
        // The documented boundary. A rebind cannot climb out of an async helper,
        // so the seeded holder dies with it.
        static async Task Retrieve()
        {
            await Task.Yield();
            Retrieval.DeclareRetrieval(new[] { "declared deep" }, "corpus-a");
        }

        RetrievalBus.Clear();
        await Retrieve();

        Assert.False(RetrievalBus.HasEvents());
    }

    [Fact]
    public async Task A_scope_carries_a_declaration_made_inside_an_awaited_helper()
    {
        // The same call that fails above succeeds under a scope, because the
        // scope seeded the holder HERE and the helper mutates it in place. This
        // is the pair the contract's "wrap them in a scope" sentence describes.
        static async Task Retrieve()
        {
            await Task.Yield();
            Retrieval.DeclareRetrieval(new[] { "declared deep" }, "corpus-b");
        }

        RetrievalBus.Clear();
        using (Retrieval.RetrievalScope(new[] { "declared at the top" }, "corpus-a"))
        {
            await Retrieve();

            var events = RetrievalBus.Snapshot();
            Assert.Equal(2, events.Count);
            Assert.Contains(events, e => e.Corpus == "corpus-b");
        }
    }

    [Fact]
    public async Task A_scope_restores_the_previous_bus_on_dispose()
    {
        RetrievalBus.Clear();
        Retrieval.DeclareRetrieval(new[] { "outer" }, "corpus-outer");

        using (Retrieval.RetrievalScope(new[] { "inner" }, "corpus-inner"))
        {
            await Task.Yield();
            Assert.Single(RetrievalBus.Snapshot());
            Assert.Equal("corpus-inner", RetrievalBus.Snapshot()[0].Corpus);
        }

        // The scope's declaration is gone; the outer one is back. A retrieval
        // that ended up unused cannot attach itself to a later call.
        Assert.Single(RetrievalBus.Snapshot());
        Assert.Equal("corpus-outer", RetrievalBus.Snapshot()[0].Corpus);
        RetrievalBus.Clear();
    }

    [Fact]
    public async Task Eight_concurrent_flows_each_see_only_their_own_declaration()
    {
        var observed = new ConcurrentBag<(int Task, int Count, string Corpus)>();

        await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(async () =>
        {
            await Task.Delay(1 + (i % 3));
            Retrieval.DeclareRetrieval(new[] { $"nonce-{i}" }, $"corpus-{i}");
            await Task.Delay(1 + ((i + 1) % 3));

            var events = RetrievalBus.Snapshot();
            observed.Add((i, events.Count, events.Count == 1 ? events[0].Corpus : "<mixed>"));
        })));

        Assert.Equal(8, observed.Count);
        foreach (var (task, count, corpus) in observed)
        {
            Assert.True(count == 1, $"task {task} saw {count} declarations, not 1");
            Assert.Equal($"corpus-{task}", corpus);
        }
    }
}
