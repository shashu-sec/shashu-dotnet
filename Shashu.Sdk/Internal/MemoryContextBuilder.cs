using System.Collections.Immutable;
using System.Linq;
using System.Text.Json.Nodes;

namespace Shashu.Internal;

internal static class MemoryContextBuilder
{
    internal static readonly ImmutableArray<string> Sources = ImmutableArray.Create(
        "sdk", "manual", "langchain", "llamaindex", "tool", "otel", "n8n",
        "provider", "hosted", "other");

    internal static readonly ImmutableArray<string> Scopes = ImmutableArray.Create(
        "agent", "crew", "tenant");

    internal static string RequireScope(string? scope)
    {
        var value = string.IsNullOrWhiteSpace(scope) ? "agent" : scope.Trim();
        if (!Scopes.Contains(value))
        {
            throw new ArgumentException(
                $"DeclareMemory scope='{scope}' is not a memory_context.v1 value. "
                + $"Use one of: {string.Join(", ", Scopes)}.",
                nameof(scope));
        }
        return value;
    }

    internal static string RequireSource(string source)
    {
        if (!Sources.Contains(source))
        {
            throw new ArgumentException(
                $"DeclareMemory source='{source}' is not a memory_context.v1 value. "
                + $"Use one of: {string.Join(", ", Sources)}.",
                nameof(source));
        }
        return source;
    }

    internal static JsonObject? Build(ImmutableList<MemoryEvent> events)
    {
        if (events.IsEmpty) return null;
        var items = new JsonArray();
        var stores = new List<string>();
        var sources = new HashSet<string>();
        var scope = "agent";
        var seen = new HashSet<string>();
        foreach (var evt in events)
        {
            if (!stores.Contains(evt.Store)) stores.Add(evt.Store);
            sources.Add(evt.Source);
            scope = evt.Scope;
            foreach (var item in evt.Items)
            {
                if (!seen.Add(item.Id + "\0" + item.Preview)) continue;
                items.Add(new JsonObject
                {
                    ["id"] = item.Id,
                    ["preview"] = item.Preview,
                });
            }
        }
        if (items.Count == 0) return null;
        while (items.Count > RetrievalBus.MaxDocs) items.RemoveAt(0);
        var store = string.Join("+", stores);
        if (store.Length > RetrievalBus.MaxCorpusChars)
            store = store[..RetrievalBus.MaxCorpusChars];
        return new JsonObject
        {
            ["v"] = 1,
            ["store"] = store,
            ["scope"] = scope,
            ["source"] = sources.Count == 1 ? sources.First() : "other",
            ["items"] = items,
        };
    }
}

internal sealed record MemoryEvent(
    string Store,
    string Scope,
    string Source,
    ImmutableList<RagContextDoc> Items);
