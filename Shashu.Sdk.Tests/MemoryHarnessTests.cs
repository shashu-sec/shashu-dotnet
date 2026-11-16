using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Shashu.Internal;
using Shashu.Routing;
using Shashu.Tests.Harness;
using Xunit;

namespace Shashu.Tests;

public class MemoryHarnessTests
{
    private static (HttpClient Client, MockGateway Gateway) Wire()
    {
        var gateway = new MockGateway();
        var handler = new ShashuRoutingHandler(
            "sk-trustgate-test",
            gateway,
            new ShashuRoutingOptions { BaseUrl = TestGateway.Uri });
        return (new HttpClient(handler), gateway);
    }

    private static HttpRequestMessage Completion(JsonObject? extra = null)
    {
        var body = new JsonObject
        {
            ["model"] = "gpt-4o-mini",
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "user", ["content"] = "hello" }),
        };
        if (extra is not null)
            body["trustgate_metadata"] = extra;
        return new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions")
        {
            Content = new StringContent(
                body.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
                Encoding.UTF8,
                "application/json"),
        };
    }

    [Fact]
    public async Task DeclareMemory_attaches_memory_context_v1()
    {
        RetrievalBus.Clear();
        Memory.DeclareMemory(
            new[] { new { Id = "note-1", Preview = "User prefers UTC." } },
            store: "agent_notes",
            scope: "agent",
            source: "sdk");
        var (client, gateway) = Wire();
        using (client)
        {
            using var _ = await client.SendAsync(Completion());
            var ctx = gateway.Single().MemoryContext;
            Assert.NotNull(ctx);
            Assert.Equal(1, ctx!["v"]!.GetValue<int>());
            Assert.Equal("agent_notes", ctx["store"]!.GetValue<string>());
            Assert.Equal("agent", ctx["scope"]!.GetValue<string>());
            Assert.Equal("sdk", ctx["source"]!.GetValue<string>());
            Assert.Equal("note-1", ctx["items"]![0]!["id"]!.GetValue<string>());
            Assert.Null(gateway.Single().RagContext);
        }
    }

    [Fact]
    public async Task MemoryScope_binds_the_unit_of_work()
    {
        RetrievalBus.Clear();
        var (client, gateway) = Wire();
        using (client)
        {
            using (Memory.MemoryScope(
                       new[] { new { Id = "n2", Preview = "crew note" } },
                       store: "crew_store",
                       scope: "crew",
                       source: "tool"))
            {
                using var _ = await client.SendAsync(Completion());
            }
            var ctx = gateway.Single().MemoryContext;
            Assert.Equal("crew_store", ctx!["store"]!.GetValue<string>());
            Assert.Equal("crew", ctx["scope"]!.GetValue<string>());
            Assert.Equal("tool", ctx["source"]!.GetValue<string>());
        }
    }

    [Fact]
    public async Task Empty_items_send_no_memory_context()
    {
        RetrievalBus.Clear();
        Memory.DeclareMemory(Array.Empty<object>(), store: "agent_notes");
        var (client, gateway) = Wire();
        using (client)
        {
            using var _ = await client.SendAsync(Completion());
            Assert.False(gateway.Single().HasShashuMetadata);
            Assert.Null(gateway.Single().MemoryContext);
        }
    }

    [Fact]
    public async Task Explicit_memory_context_wins()
    {
        RetrievalBus.Clear();
        Memory.DeclareMemory(
            new[] { new { Id = "ambient", Preview = "ambient" } },
            store: "ambient_store");
        var (client, gateway) = Wire();
        using (client)
        {
            var extra = new JsonObject
            {
                ["memory_context"] = new JsonObject
                {
                    ["v"] = 1,
                    ["store"] = "explicit_store",
                    ["scope"] = "agent",
                    ["source"] = "manual",
                    ["items"] = new JsonArray(new JsonObject { ["id"] = "E", ["preview"] = "explicit" }),
                },
            };
            using var _ = await client.SendAsync(Completion(extra));
            Assert.Equal("explicit_store", gateway.Single().MemoryContext!["store"]!.GetValue<string>());
        }
    }
}
