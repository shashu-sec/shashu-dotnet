using System.Text.Json;
using Shashu;
using Shashu.Chat;
using Xunit;
using Shashu.Tests.Harness;

namespace Shashu.Tests;

/// <summary>
/// Verifies the SDK-3 tool-call fields on <c>ChatMessage</c> serialise to the
/// exact OpenAI message shape the gateway's <c>ingress-tool</c> scanner parses
/// (<c>msg["tool_calls"][i]["function"]["name"]/["arguments"]</c>, with
/// <c>arguments</c> a JSON-encoded STRING) — and, critically, that messages
/// built the pre-SDK-3 way (<c>new ChatMessage(role, content)</c>) still
/// serialise byte-identically with no tool keys emitted.
/// </summary>
public class ToolCallTests
{
    private static async Task<string> CaptureBodyAsync(ChatCompletionRequest request)
    {
        var capture = new JsonBodyCapturingHandler();
        using var http = new HttpClient(capture) { BaseAddress = new Uri("https://test.invalid/") };
        using var client = new ShashuClient(
            new ShashuConfig { BaseUrl = TestGateway.Uri, ApiKey = "sk-trustgate-test" }, http);
        await client.Chat.Completions.CreateAsync(request);
        Assert.NotNull(capture.LastRequestBody);
        return capture.LastRequestBody!;
    }

    [Fact]
    public async Task BackCompat_two_arg_message_serialises_byte_identical_with_no_tool_keys()
    {
        var body = await CaptureBodyAsync(new ChatCompletionRequest
        {
            Model = "gpt-4o",
            Messages = new[] { new ChatMessage("user", "hi") },
        });

        using var doc = JsonDocument.Parse(body);
        // Golden-string assert on the raw wire text of the message object.
        Assert.Equal("""{"role":"user","content":"hi"}""",
            doc.RootElement.GetProperty("messages")[0].GetRawText());
    }

    [Fact]
    public void BackCompat_holds_under_plain_default_serializer_options_too()
    {
        // The client's serializer omits nulls globally; the per-property
        // WhenWritingNull attributes must make omission hold even for callers
        // serialising a ChatMessage themselves with default options.
        Assert.Equal("""{"role":"user","content":"hi"}""",
            JsonSerializer.Serialize(new ChatMessage("user", "hi")));
    }

    [Fact]
    public async Task Assistant_tool_calls_serialise_to_openai_standard_shape()
    {
        var body = await CaptureBodyAsync(new ChatCompletionRequest
        {
            Model = "gpt-4o",
            Messages = new[]
            {
                new ChatMessage("user", "weather in Paris?"),
                new ChatMessage("assistant", "")
                {
                    ToolCalls = new[]
                    {
                        new ToolCall
                        {
                            Id = "call_abc123",
                            Function = new FunctionCall("get_weather", """{"city":"Paris"}"""),
                        },
                    },
                },
            },
        });

        using var doc = JsonDocument.Parse(body);
        var toolCalls = doc.RootElement.GetProperty("messages")[1].GetProperty("tool_calls");
        Assert.Equal(1, toolCalls.GetArrayLength());
        var call = toolCalls[0];
        Assert.Equal("call_abc123", call.GetProperty("id").GetString());
        Assert.Equal("function", call.GetProperty("type").GetString());
        Assert.Equal("get_weather", call.GetProperty("function").GetProperty("name").GetString());
        Assert.Equal("""{"city":"Paris"}""",
            call.GetProperty("function").GetProperty("arguments").GetString());
    }

    [Fact]
    public async Task Tool_factory_serialises_role_tool_with_tool_call_id_name_content()
    {
        var body = await CaptureBodyAsync(new ChatCompletionRequest
        {
            Model = "gpt-4o",
            Messages = new[] { ChatMessage.Tool("call_abc123", "18°C, sunny", "get_weather") },
        });

        using var doc = JsonDocument.Parse(body);
        var msg = doc.RootElement.GetProperty("messages")[0];
        Assert.Equal("tool", msg.GetProperty("role").GetString());
        Assert.Equal("call_abc123", msg.GetProperty("tool_call_id").GetString());
        Assert.Equal("get_weather", msg.GetProperty("name").GetString());
        Assert.Equal("18°C, sunny", msg.GetProperty("content").GetString());
        Assert.False(msg.TryGetProperty("tool_calls", out _));
    }

    [Fact]
    public async Task Tool_factory_without_name_omits_the_name_key()
    {
        var body = await CaptureBodyAsync(new ChatCompletionRequest
        {
            Model = "gpt-4o",
            Messages = new[] { ChatMessage.Tool("call_abc123", "18°C, sunny") },
        });

        using var doc = JsonDocument.Parse(body);
        var msg = doc.RootElement.GetProperty("messages")[0];
        Assert.Equal("call_abc123", msg.GetProperty("tool_call_id").GetString());
        Assert.False(msg.TryGetProperty("name", out _));
    }

    [Fact]
    public async Task Arguments_stays_a_json_encoded_string_not_reencoded_as_object()
    {
        var body = await CaptureBodyAsync(new ChatCompletionRequest
        {
            Model = "gpt-4o",
            Messages = new[]
            {
                new ChatMessage("assistant", "")
                {
                    ToolCalls = new[]
                    {
                        new ToolCall
                        {
                            Id = "call_1",
                            Function = new FunctionCall(
                                "run_query", """{"sql":"SELECT * FROM users WHERE id = 1"}"""),
                        },
                    },
                },
            },
        });

        using var doc = JsonDocument.Parse(body);
        var arguments = doc.RootElement.GetProperty("messages")[0]
            .GetProperty("tool_calls")[0].GetProperty("function").GetProperty("arguments");
        // Must be a STRING on the wire (the gateway json.loads it), not a nested object.
        Assert.Equal(JsonValueKind.String, arguments.ValueKind);
        using var inner = JsonDocument.Parse(arguments.GetString()!);
        Assert.Equal("SELECT * FROM users WHERE id = 1",
            inner.RootElement.GetProperty("sql").GetString());
    }

    [Fact]
    public async Task Gateway_extraction_shape_conformance_across_a_full_tool_round_trip()
    {
        // Read the body the way the gateway's _extract_tool_calls does: walk
        // messages, take msg["tool_calls"], then tc["function"]["name"] and
        // tc["function"]["arguments"] per call.
        var body = await CaptureBodyAsync(new ChatCompletionRequest
        {
            Model = "gpt-4o",
            Messages = new[]
            {
                new ChatMessage("user", "weather in Paris and London?"),
                new ChatMessage("assistant", "")
                {
                    ToolCalls = new[]
                    {
                        new ToolCall
                        {
                            Id = "call_1",
                            Function = new FunctionCall("get_weather", """{"city":"Paris"}"""),
                        },
                        new ToolCall
                        {
                            Id = "call_2",
                            Function = new FunctionCall("get_weather", """{"city":"London"}"""),
                        },
                    },
                },
                ChatMessage.Tool("call_1", "18°C", "get_weather"),
                ChatMessage.Tool("call_2", "15°C", "get_weather"),
            },
        });

        using var doc = JsonDocument.Parse(body);
        var extracted = new List<(string Name, string Arguments)>();
        foreach (var msg in doc.RootElement.GetProperty("messages").EnumerateArray())
        {
            if (!msg.TryGetProperty("tool_calls", out var toolCalls))
                continue;
            foreach (var tc in toolCalls.EnumerateArray())
            {
                var fn = tc.GetProperty("function");
                extracted.Add((
                    fn.GetProperty("name").GetString()!,
                    fn.GetProperty("arguments").GetString()!));
            }
        }

        Assert.Equal(2, extracted.Count);
        Assert.All(extracted, e => Assert.Equal("get_weather", e.Name));
        Assert.Equal("""{"city":"Paris"}""", extracted[0].Arguments);
        Assert.Equal("""{"city":"London"}""", extracted[1].Arguments);
    }
}
