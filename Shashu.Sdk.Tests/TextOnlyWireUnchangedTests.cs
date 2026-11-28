using System.Net;
using System.Text.Json;
using Shashu;
using Shashu.Chat;
using Shashu.Tests.Harness;
using Xunit;

namespace Shashu.Tests;

/// <summary>
/// Text-only messages keep the 1.2.0 bytes. The golden file and the literals
/// below were captured from the 1.2.0 sources before messages learned to carry
/// parts.
/// </summary>
public class TextOnlyWireUnchangedTests
{
    private const string Text = "Résumé — a+b <x> & 'q' \"d\" / \\ 😀\n\tend \u001f";

    private const string DefaultSerializer120 =
        "{\"role\":\"user\",\"content\":\"h\\u00E9\\u002B\",\"name\":\"n\"}";

    private sealed class Capture : HttpMessageHandler
    {
        public byte[]? Body;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"id\":\"x\",\"choices\":[]}"),
            };
        }
    }

    [Fact]
    public async Task Text_only_request_is_byte_identical_to_1_2_0()
    {
        var capture = new Capture();
        using var http = new HttpClient(capture) { BaseAddress = new Uri("https://test.invalid/") };
        using var client = new ShashuClient(
            new ShashuConfig { BaseUrl = TestGateway.Uri, ApiKey = "shashu_sk_golden" }, http);

        await client.Chat.Completions.CreateAsync(new ChatCompletionRequest
        {
            Model = "gpt-4o-mini",
            Messages = new[]
            {
                new ChatMessage("system", "sys"),
                new ChatMessage("user", Text),
                new ChatMessage("assistant", "")
                {
                    ToolCalls = new[] { new ToolCall { Id = "c1", Function = new FunctionCall("f", "{\"a\":1}") } },
                },
                ChatMessage.Tool("c1", "r", "f"),
                new ChatMessage("assistant", null!),
            },
        });

        var expected = await File.ReadAllBytesAsync(
            Path.Combine(AppContext.BaseDirectory, "Golden", "text_only.dotnet-1.2.0.json"));
        Assert.Equal(expected, capture.Body);
    }

    [Fact]
    public void A_callers_own_serializer_writes_what_it_wrote_on_1_2_0()
    {
        var message = new ChatMessage("user", "hé+") { Name = "n" };
        Assert.Equal(DefaultSerializer120, JsonSerializer.Serialize(message));
        Assert.Equal(
            DefaultSerializer120,
            JsonSerializer.Serialize(message, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.Equal(
            "{\"role\":\"assistant\",\"content\":null}",
            JsonSerializer.Serialize(new ChatMessage("assistant", null!)));
    }

    [Fact]
    public void Round_trip_and_with_expressions_still_work()
    {
        var message = JsonSerializer.Deserialize<ChatMessage>(
            "{\"role\":\"tool\",\"content\":\"r\",\"tool_call_id\":\"c1\",\"name\":\"f\",\"extra\":{\"a\":[1]}}")!;
        Assert.Equal(ChatMessage.Tool("c1", "r", "f"), message);
        Assert.Equal("other", (message with { Content = "other" }).Content);
        var (role, content) = message;
        Assert.Equal(("tool", "r"), (role, content));
    }
}
