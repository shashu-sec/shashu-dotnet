using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Shashu;
using Shashu.Chat;
using Xunit;
using Shashu.Tests.Harness;

namespace Shashu.Tests;

/// <summary>
/// Verifies the <c>RagContext</c> + <c>ShashuMetadata</c> fields
/// serialise correctly under <c>shashu_metadata.rag_context</c> in
/// the request body — the contract the gateway's <c>ingress-rag</c>
/// Shashu scanner reads from.
/// </summary>
public class RagContextTests
{
    [Fact]
    public async Task RagContext_serialises_under_shashu_metadata_rag_context()
    {
        var capture = new JsonBodyCapturingHandler();
        using var http = new HttpClient(capture) { BaseAddress = new Uri("https://test.invalid/") };
        using var client = new ShashuClient(
            new ShashuConfig { BaseUrl = TestGateway.Uri, ApiKey = "sk-trustgate-test" }, http);

        await client.Chat.Completions.CreateAsync(new ChatCompletionRequest
        {
            Model = "gpt-4o",
            Messages = new[] { new ChatMessage("user", "what's the refinance rate?") },
            RagContext = new RagContext
            {
                Corpus = "policy_docs_v2",
                Query = "refinance",
                Docs = new[]
                {
                    new RagDoc("POL-1", 0.92, "Refinance must occur within..."),
                    new RagDoc("POL-2", 0.81, "Eligibility requires..."),
                },
            },
        });

        Assert.NotNull(capture.LastRequestBody);
        using var doc = JsonDocument.Parse(capture.LastRequestBody!);
        var meta = doc.RootElement.GetProperty("shashu_metadata");
        var rag = meta.GetProperty("rag_context");
        Assert.Equal("policy_docs_v2", rag.GetProperty("corpus").GetString());
        Assert.Equal("refinance", rag.GetProperty("query").GetString());
        Assert.Equal(2, rag.GetProperty("docs").GetArrayLength());
        Assert.Equal("POL-1", rag.GetProperty("docs")[0].GetProperty("id").GetString());
        Assert.Equal(0.92, rag.GetProperty("docs")[0].GetProperty("score").GetDouble());
        Assert.Contains("Refinance", rag.GetProperty("docs")[0].GetProperty("preview").GetString());
    }

    [Fact]
    public async Task ShashuMetadata_freeform_serialises_alongside_RagContext()
    {
        var capture = new JsonBodyCapturingHandler();
        using var http = new HttpClient(capture) { BaseAddress = new Uri("https://test.invalid/") };
        using var client = new ShashuClient(
            new ShashuConfig { BaseUrl = TestGateway.Uri, ApiKey = "sk-trustgate-test" }, http);

        await client.Chat.Completions.CreateAsync(new ChatCompletionRequest
        {
            Model = "gpt-4o",
            Messages = new[] { new ChatMessage("user", "hi") },
            RagContext = new RagContext { Corpus = "c", Query = "q" },
            ShashuMetadata = new Dictionary<string, object?>
            {
                ["workflow_step"] = "Final_Summary",
                ["custom_field"] = "value",
            },
        });

        using var doc = JsonDocument.Parse(capture.LastRequestBody!);
        var meta = doc.RootElement.GetProperty("shashu_metadata");
        Assert.Equal("Final_Summary", meta.GetProperty("workflow_step").GetString());
        Assert.Equal("value", meta.GetProperty("custom_field").GetString());
        Assert.Equal("c", meta.GetProperty("rag_context").GetProperty("corpus").GetString());
    }

    [Fact]
    public async Task No_RagContext_or_ShashuMetadata_omits_the_field_entirely()
    {
        var capture = new JsonBodyCapturingHandler();
        using var http = new HttpClient(capture) { BaseAddress = new Uri("https://test.invalid/") };
        using var client = new ShashuClient(
            new ShashuConfig { BaseUrl = TestGateway.Uri, ApiKey = "sk-trustgate-test" }, http);

        await client.Chat.Completions.CreateAsync(new ChatCompletionRequest
        {
            Model = "gpt-4o",
            Messages = new[] { new ChatMessage("user", "hi") },
        });

        using var doc = JsonDocument.Parse(capture.LastRequestBody!);
        // Vital: when nothing is set, the field must be omitted, not emitted as `"shashu_metadata":null`.
        Assert.False(doc.RootElement.TryGetProperty("shashu_metadata", out _));
    }

    [Fact]
    public async Task Caller_explicit_rag_context_in_ShashuMetadata_wins_over_typed_RagContext()
    {
        // If the caller stashes their own rag_context inside ShashuMetadata
        // (escape hatch for non-standard shapes), it must NOT be clobbered by
        // the typed RagContext setter.
        var capture = new JsonBodyCapturingHandler();
        using var http = new HttpClient(capture) { BaseAddress = new Uri("https://test.invalid/") };
        using var client = new ShashuClient(
            new ShashuConfig { BaseUrl = TestGateway.Uri, ApiKey = "sk-trustgate-test" }, http);

        await client.Chat.Completions.CreateAsync(new ChatCompletionRequest
        {
            Model = "gpt-4o",
            Messages = new[] { new ChatMessage("user", "hi") },
            RagContext = new RagContext { Corpus = "should-be-ignored" },
            ShashuMetadata = new Dictionary<string, object?>
            {
                ["rag_context"] = new { corpus = "from-caller-explicit" },
            },
        });

        using var doc = JsonDocument.Parse(capture.LastRequestBody!);
        var rag = doc.RootElement.GetProperty("shashu_metadata").GetProperty("rag_context");
        Assert.Equal("from-caller-explicit", rag.GetProperty("corpus").GetString());
    }
}

/// <summary>Captures the request body string for inspection.</summary>
internal sealed class JsonBodyCapturingHandler : HttpMessageHandler
{
    public string? LastRequestBody { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Content is not null)
            LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        // Return a minimal but valid ChatCompletionResponse-shaped body.
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new
            {
                id = "fake-1",
                @object = "chat.completion",
                created = 1,
                model = "gpt-4o",
                choices = new[]
                {
                    new
                    {
                        index = 0,
                        message = new { role = "assistant", content = "ok" },
                        finish_reason = "stop",
                    },
                },
                usage = new { prompt_tokens = 1, completion_tokens = 1, total_tokens = 2 },
            }),
        };
    }
}
