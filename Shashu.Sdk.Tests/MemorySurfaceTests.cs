using System.Reflection;
using Shashu.Internal;
using Shashu.Tests.Harness;
using Xunit;

namespace Shashu.Tests;

/// <summary>
/// Holds the public memory surface to <c>contracts/sdk-surface.v1.md</c>.
/// Sibling of the Java SDK's <c>MemorySurfaceTests</c>.
/// </summary>
public class MemorySurfaceTests
{
    private static readonly Type Holder =
        typeof(ShashuClient).Assembly.GetType("Shashu.Memory")
        ?? throw new InvalidOperationException("Shashu.Memory is missing");

    [Fact]
    public void There_are_exactly_two_public_memory_members_and_no_third()
    {
        var names = Holder
            .GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Select(m => m.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(["DeclareMemory", "MemoryScope"], names);
    }

    [Fact]
    public void The_contract_row_names_this_holder()
    {
        var page = File.ReadAllText(Path.Combine(
            RagContextSchema.Directory.FullName, "sdk-surface.v1.md"));
        Assert.Contains("Memory.MemoryScope(items, store)", page);
        Assert.Contains("Shashu.Memory.DeclareMemory(items, store:", page);
    }

    [Fact]
    public void MemoryScope_returns_IDisposable()
    {
        Assert.True(typeof(IDisposable).IsAssignableFrom(
            Holder.GetMethod("MemoryScope")!.ReturnType));
    }

    [Fact]
    public void Argument_defaults_match_memory_context_v1()
    {
        var parameters = Holder.GetMethod("DeclareMemory")!.GetParameters();
        Assert.Equal(["items", "store", "scope", "source"], parameters.Select(p => p.Name!).ToArray());
        Assert.False(parameters[0].HasDefaultValue);
        Assert.False(parameters[1].HasDefaultValue);
        Assert.Equal("agent", parameters[2].DefaultValue);
        Assert.Equal("sdk", parameters[3].DefaultValue);
    }

    [Fact]
    public void Empty_items_do_not_validate_store()
    {
        Memory.DeclareMemory(Array.Empty<object>(), store: "");
        Memory.DeclareMemory(null, store: "");
    }

    [Fact]
    public void Blank_store_with_an_item_throws()
    {
        Assert.Throws<ArgumentException>(() =>
            Memory.DeclareMemory(new[] { new { Id = "m1", Preview = "note" } }, store: "  "));
    }

    [Fact]
    public void Scope_and_source_are_memory_context_v1_enums()
    {
        var item = new[] { new { Id = "m1", Preview = "note" } };
        Assert.Throws<ArgumentException>(() =>
            Memory.DeclareMemory(item, store: "agent_notes", scope: "session"));
        Assert.Throws<ArgumentException>(() =>
            Memory.DeclareMemory(item, store: "agent_notes", source: "vectorstore"));
        Memory.DeclareMemory(item, store: "agent_notes", scope: "crew", source: "tool");
        Memory.DeclareMemory(item, store: "agent_notes", scope: "tenant", source: "hosted");
    }

    [Fact]
    public void Folded_context_is_schema_v1_and_dedupes()
    {
        var evt = new MemoryEvent(
            "agent_notes",
            "agent",
            "sdk",
            RagContextBuilder.CoerceDocs(new[]
            {
                new { Id = "m1", Preview = "alpha" },
                new { Id = "m1", Preview = "alpha" },
            }));
        var ctx = MemoryContextBuilder.Build(System.Collections.Immutable.ImmutableList.Create(evt));
        Assert.Equal(1, ctx!["v"]!.GetValue<int>());
        Assert.Equal("agent_notes", ctx["store"]!.GetValue<string>());
        Assert.Equal("agent", ctx["scope"]!.GetValue<string>());
        Assert.Equal("sdk", ctx["source"]!.GetValue<string>());
        Assert.Single(ctx["items"]!.AsArray());
        Assert.Null(MemoryContextBuilder.Build(System.Collections.Immutable.ImmutableList<MemoryEvent>.Empty));
    }
}
