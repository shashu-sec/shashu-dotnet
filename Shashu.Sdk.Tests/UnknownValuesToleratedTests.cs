using System.Net;
using System.Text;
using Shashu.Chat;
using Shashu.Tests.Harness;
using Xunit;

namespace Shashu.Tests;

/// <summary>
/// Unknown values the gateway may send tomorrow are read today, never thrown on.
/// </summary>
/// <remarks>
/// Written BEFORE the FILE-06 accessors, against the surface that already
/// existed, so it records whether this SDK tolerated a made-up <c>code</c>, a
/// made-up reason, a made-up field and a made-up attachment state before
/// anything changed — the question for .NET being whether any of those is bound
/// to an enum or a strict <c>System.Text.Json</c> type. It uses no FILE-06 name,
/// so it runs on either side of that change.
/// </remarks>
public class UnknownValuesToleratedTests
{
    internal const string Envelope =
        "{\"decision\":\"refuse\",\"code\":\"made_up_code_zz\",\"retryable\":false,\"id\":\"rec-1\","
        + "\"inspection\":{\"input\":\"checked\",\"output\":\"checked\",\"detectors\":{\"made_up_detector\":\"zz\"}},"
        + "\"versions\":{\"contract\":\"2099-01-01\"},\"message\":\"m\",\"made_up_top_level_field\":{\"nested\":[1,2]},"
        + "\"attachments\":{\"total\":1,\"blocked\":1,\"made_up_count\":3,\"items\":[{\"state\":\"quarantined_zz\","
        + "\"reason\":\"made_up_reason_zz\",\"made_up_field\":true}]},"
        + "\"memory\":{\"coverage\":\"made_up_zz\",\"items\":[{\"kind\":\"zz\",\"reason\":\"made_up_reason_zz\"}]},"
        + "\"route_policy\":{\"route\":\"r\",\"category\":\"zz\",\"reason\":\"made_up_reason_zz\",\"extra\":1}}";

    internal static readonly Dictionary<string, string> Headers = new()
    {
        ["x-shashu-decision"] = "refuse",
        ["x-shashu-code"] = "made_up_code_zz",
        ["x-shashu-attachments"] = "total=1;blocked=1;made_up=2",
        ["x-shashu-memory"] = "writes=1;made_up=2",
        ["x-shashu-made-up-header"] = "zz",
    };

    private static readonly ChatCompletionRequest Request = new()
    {
        Model = "gpt-4o",
        Messages = new[] { new ChatMessage("user", "hi") },
    };

    [Fact]
    public void DecisionFrom_reads_made_up_values_without_throwing()
    {
        var d = Shashu.DecisionFrom("{\"error\":{\"message\":\"m\",\"shashu\":" + Envelope + "}}", Headers);
        Assert.NotNull(d);
        Assert.Equal("refuse", d!.Decision);
        Assert.Equal("made_up_code_zz", d.Code);
    }

    [Fact]
    public void A_made_up_decision_is_still_a_block()
    {
        var body = "{\"shashu\":" + Envelope.Replace("\"refuse\"", "\"made_up_decision_zz\"") + "}";
        Assert.Equal("made_up_decision_zz", Shashu.DecisionFrom(body, null)!.Decision);
        Assert.True(Shashu.IsBlock(body));
    }

    [Fact]
    public async Task The_client_throws_the_type_it_always_did()
    {
        using var client = Client(HttpStatusCode.Forbidden,
            "{\"error\":{\"message\":\"m\",\"type\":\"permission_error\",\"code\":\"made_up_code_zz\",\"shashu\":"
            + Envelope + "}}", Headers);
        var ex = await Assert.ThrowsAnyAsync<ShashuException>(() => client.Chat.Completions.CreateAsync(Request));
        Assert.IsAssignableFrom<ShashuPolicyViolation>(ex);
        Assert.Equal("made_up_code_zz", Shashu.DecisionOf(ex)!.Code);
    }

    [Fact]
    public async Task A_200_with_made_up_values_is_delivered()
    {
        using var client = Client(HttpStatusCode.OK,
            "{\"id\":\"c1\",\"object\":\"chat.completion\",\"created\":1,\"model\":\"gpt-4o\","
            + "\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"ok\"},\"finish_reason\":\"stop\"}],"
            + "\"made_up_body_field\":{\"x\":1},\"shashu\":" + Envelope.Replace("\"refuse\"", "\"allow\"") + "}",
            new Dictionary<string, string> { ["x-shashu-decision"] = "allow" });
        var resp = await client.Chat.Completions.CreateAsync(Request);
        Assert.Equal("ok", resp.Choices[0].Message.Content);
        Assert.Equal("made_up_code_zz", Shashu.DecisionOf(resp)!.Code);
    }

    /// <summary>
    /// SDKB-5's shape (<c>created: null</c> on every chunk) with the envelope
    /// riding frame one, carrying a file block full of made-up values. A
    /// delivered stream with attachments must not throw.
    /// </summary>
    [Fact]
    public async Task A_stream_with_created_null_and_a_made_up_frame_one_envelope_does_not_throw()
    {
        var sse =
            "data: {\"id\":\"c\",\"object\":\"chat.completion.chunk\",\"created\":null,\"model\":\"gpt-4o\","
            + "\"shashu\":" + Envelope.Replace("\"refuse\"", "\"not_checked\"") + ","
            + "\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"\"},\"finish_reason\":null}]}\n\n"
            + "data: {\"id\":\"c\",\"object\":\"chat.completion.chunk\",\"created\":null,\"model\":\"gpt-4o\","
            + "\"choices\":[{\"index\":0,\"delta\":{\"content\":\"Paris.\"},\"finish_reason\":\"stop\"}]}\n\n"
            + "data: [DONE]\n\n";
        using var client = Client(HttpStatusCode.OK, sse, Headers, "text/event-stream");
        var collected = new List<ChatCompletionChunk>();
        await foreach (var chunk in client.Chat.Completions.CreateStreamAsync(new ChatCompletionRequest
        {
            Model = "gpt-4o",
            Messages = new[] { new ChatMessage("user", "hi") },
            Stream = true,
        }))
        {
            collected.Add(chunk);
        }
        Assert.Equal(2, collected.Count);
        Assert.Null(collected[0].Created);
        Assert.Equal("Paris.", collected[1].Choices[0].Delta.Content);
    }

    internal static ShashuClient Client(
        HttpStatusCode status, string body, IReadOnlyDictionary<string, string> headers,
        string contentType = "application/json")
    {
        var http = new HttpClient(new CannedResponseHandler(status, body, headers, contentType))
        {
            BaseAddress = TestGateway.Uri,
        };
        return new ShashuClient(
            new ShashuConfig { BaseUrl = TestGateway.Uri, ApiKey = "sk-trustgate-test", MaxRetries = 0 }, http);
    }
}

/// <summary>Answers every request with one fixed status, body and header set.</summary>
internal sealed class CannedResponseHandler : HttpMessageHandler
{
    private readonly HttpStatusCode _status;
    private readonly string _body;
    private readonly IReadOnlyDictionary<string, string> _headers;
    private readonly string _contentType;

    public CannedResponseHandler(
        HttpStatusCode status, string body, IReadOnlyDictionary<string, string> headers, string contentType)
    {
        _status = status;
        _body = body;
        _headers = headers;
        _contentType = contentType;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = new HttpResponseMessage(_status)
        {
            Content = new StringContent(_body, Encoding.UTF8, _contentType),
        };
        foreach (var (k, v) in _headers)
            response.Headers.TryAddWithoutValidation(k, v);
        return Task.FromResult(response);
    }
}
