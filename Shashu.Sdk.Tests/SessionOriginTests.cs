using System.Net;
using System.Net.Http.Json;
using Shashu;
using Shashu.Chat;
using Shashu.Tracing;
using Xunit;
using Shashu.Tests.Harness;

namespace Shashu.Tests;

/// <summary>
/// One client is one session. A captured inbound session overrides the
/// construction-time default. A per-request sessionId is an instruction
/// that wins, and applies to that request only.
/// </summary>
public class SessionOriginTests
{
    [Fact]
    public async Task Origin_sends_a_minted_session_and_reuses_it()
    {
        var sent = new List<string?>();
        using var client = BuildClient(sent);

        await Call(client);
        await Call(client);

        Assert.Equal(2, sent.Count);
        Assert.False(string.IsNullOrWhiteSpace(sent[0]));
        Assert.Equal(sent[0], sent[1]);
        Assert.Equal(client.SessionId, sent[0]);
    }

    [Fact]
    public async Task Two_clients_are_two_sessions()
    {
        var aSent = new List<string?>();
        var bSent = new List<string?>();
        using var a = BuildClient(aSent);
        using var b = BuildClient(bSent);

        await Call(a);
        await Call(b);

        Assert.NotEqual(aSent[0], bSent[0]);
    }

    [Fact]
    public async Task Construction_session_is_a_default()
    {
        var sent = new List<string?>();
        using var client = BuildClient(sent, sessionId: "constructed");

        await Call(client);

        Assert.Equal("constructed", sent[0]);
    }

    [Fact]
    public async Task Per_request_session_is_an_instruction_that_wins_and_does_not_stick()
    {
        var sent = new List<string?>();
        using var client = BuildClient(sent, sessionId: "constructed");

        await client.Chat.Completions.CreateAsync(
            new ChatCompletionRequest
            {
                Model = "gpt-4o",
                Messages = new[] { new ChatMessage("user", "hi") },
            },
            sessionId: "per-request");
        await Call(client);

        Assert.Equal(new[] { "per-request", "constructed" }, sent);
    }

    [Fact]
    public async Task Captured_inbound_session_overrides_construction_default()
    {
        var sent = new List<string?>();
        using var client = BuildClient(sent, sessionId: "constructed");

        using (Tracer.Incoming(H(
            ("x-trustgate-trace-id", "flow"),
            ("x-trustgate-session-id", "captured"))))
        {
            await Call(client);
        }

        Assert.Equal("captured", sent[0]);
    }

    [Fact]
    public async Task Per_request_wins_over_captured_inbound()
    {
        var sent = new List<string?>();
        using var client = BuildClient(sent, sessionId: "constructed");

        using (Tracer.Incoming(H(
            ("x-trustgate-trace-id", "flow"),
            ("x-trustgate-session-id", "captured"))))
        {
            await client.Chat.Completions.CreateAsync(
                new ChatCompletionRequest
                {
                    Model = "gpt-4o",
                    Messages = new[] { new ChatMessage("user", "hi") },
                },
                sessionId: "per-request");
        }

        Assert.Equal("per-request", sent[0]);
    }

    private static ShashuClient BuildClient(List<string?> sent, string? sessionId = null)
    {
        var stub = new StubHandler((req, _) =>
        {
            sent.Add(req.Headers.TryGetValues("x-trustgate-session-id", out var v)
                ? v.Single()
                : null);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = OkBody() });
        });
        var http = new HttpClient(stub) { BaseAddress = new Uri("https://test.invalid/") };
        return new ShashuClient(
            new ShashuConfig
            {
                BaseUrl = TestGateway.Uri,
                ApiKey = "sk-trustgate-test",
                SessionId = sessionId,
            },
            http);
    }

    private static Task<ChatCompletionResponse> Call(ShashuClient client) =>
        client.Chat.Completions.CreateAsync(new ChatCompletionRequest
        {
            Model = "gpt-4o",
            Messages = new[] { new ChatMessage("user", "hi") },
        });

    private static Dictionary<string, string> H(params (string k, string v)[] pairs)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in pairs)
            d[k] = v;
        return d;
    }

    private static HttpContent OkBody() =>
        JsonContent.Create(new
        {
            id = "c1",
            @object = "chat.completion",
            created = 1,
            model = "gpt-4o",
            choices = new[]
            {
                new { index = 0, message = new { role = "assistant", content = "ok" }, finish_reason = "stop" },
            },
        });
}
