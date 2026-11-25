using System.Net;
using System.Net.Http.Json;
using System.Text;
using Shashu.Chat;
using Shashu.Routing;
using Shashu.Tests.Harness;
using Xunit;

namespace Shashu.Tests;

/// <summary>
/// The SDK declares the contract version it speaks, as the Python SDK does
/// (<c>trustgate/context.py</c> <c>DEFAULT_CONTRACT_VERSION</c>). The gateway
/// resolves an ABSENT <c>shashu-version</c> header to its legacy contract
/// (<c>core/app/contract/version.py</c> <c>resolve_version</c>), where every
/// refusal is a 200 and the body carries no decision envelope — so memory and
/// inspection details reached only the header counts.
///
/// The response bodies below are the gateway's own shapes at
/// <c>2026-09-01</c>: the delivered envelope from <c>Envelope.to_dict</c>, the
/// memory item from <c>security/memory/sink_stage.py</c> (Monitor delivers the
/// save as <c>state: flagged</c> with <c>writes_blocked: 0</c>; Block strips
/// it and counts it), and the refusal bodies from
/// <c>contract/responses.py</c>.
/// </summary>
public class ContractVersionDefaultTests
{
    private const string Header = "shashu-version";

    private static readonly ChatCompletionRequest Request = new()
    {
        Model = "gpt-4o",
        Messages = new[] { new ChatMessage("user", "hi") },
    };

    private const string Chat200 =
        "\"id\":\"chatcmpl-c0ffee\",\"object\":\"chat.completion\",\"created\":1,\"model\":\"stand-in\","
        + "\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"Noted.\"},"
        + "\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":4,\"completion_tokens\":2,\"total_tokens\":6}";

    private static string Delivered(string memory) =>
        "{" + Chat200 + ",\"shashu\":{\"decision\":\"allow\",\"retryable\":false,\"id\":\"3e348075\","
        + "\"inspection\":{\"input\":\"checked\",\"output\":\"checked\",\"detectors\":{\"memory\":\"checked\"}},"
        + "\"versions\":{\"contract\":\"2026-09-01\"},\"message\":\"Delivered.\",\"memory\":" + memory + "}}";

    private const string MonitorMemory =
        "{\"writes\":1,\"writes_blocked\":0,\"reason\":\"memory_sink_injection\",\"mode\":\"monitor\",\"items\":["
        + "{\"kind\":\"write\",\"tool\":\"save_memory\",\"state\":\"flagged\",\"reason\":\"memory_sink_injection\","
        + "\"enforcement\":\"gateway\"}]}";

    private const string BlockMemory =
        "{\"writes\":1,\"writes_blocked\":1,\"reason\":\"memory_sink_injection\",\"mode\":\"block\",\"items\":["
        + "{\"kind\":\"write\",\"tool\":\"save_memory\",\"state\":\"blocked\",\"reason\":\"memory_sink_injection\","
        + "\"enforcement\":\"gateway\"}]}";

    private static string Refusal(string type, string code, string message, string decision, string? blocks = null) =>
        "{\"error\":{\"message\":\"" + message + "\",\"type\":\"" + type + "\",\"param\":null,\"code\":\"" + code
        + "\",\"shashu\":{\"decision\":\"" + decision + "\",\"retryable\":false,\"id\":\"b510c203\","
        + "\"inspection\":{\"input\":\"checked\"},\"versions\":{\"contract\":\"2026-09-01\"},"
        + "\"message\":\"" + message + "\",\"code\":\"" + code + "\"" + (blocks is null ? "" : "," + blocks)
        + "}},\"choices\":[]}";

    private const string InjectionSentence =
        "This request looks like an attempt to override the assistant's instructions, so it was not sent to the model.";

    private const string MemorySentence =
        "A save through save_memory contained hidden instructions, so it was not stored.";

    // --- (a) the header is on the wire ---------------------------------------

    [Fact]
    public void Default_is_the_python_value()
    {
        Assert.Equal("2026-09-01", ShashuConfig.DefaultContractVersion);
        Assert.Equal("2026-09-01", new ShashuConfig().ContractVersion);
        Assert.Equal("2026-09-01", new ShashuRoutingOptions().ContractVersion);
    }

    [Fact]
    public async Task Buffered_request_carries_the_default_version()
    {
        var (client, seen) = Recording(HttpStatusCode.OK, "{" + Chat200 + "}");
        using (client)
            await client.Chat.Completions.CreateAsync(Request);

        Assert.Equal(new[] { "2026-09-01" }, seen.Values(Header));
    }

    [Fact]
    public async Task Streaming_request_carries_the_default_version()
    {
        var (client, seen) = Recording(HttpStatusCode.OK, "data: [DONE]\n\n", "text/event-stream");
        using (client)
        {
            await foreach (var _ in client.Chat.Completions.CreateStreamAsync(Request))
            {
            }
        }

        Assert.Equal(new[] { "2026-09-01" }, seen.Values(Header));
    }

    [Fact]
    public async Task Routing_handler_carries_the_default_version_to_the_gateway()
    {
        var captured = new CapturingInnerHandler();
        var handler = new ShashuRoutingHandler(
            "sk-trustgate-fake", captured, new ShashuRoutingOptions { BaseUrl = TestGateway.Uri });
        using var http = new HttpClient(handler);
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions")
        {
            Content = JsonContent.Create(new { model = "gpt-4o" }),
        };

        await http.SendAsync(req);

        Assert.True(captured.LastRequest!.Headers.TryGetValues(Header, out var values));
        Assert.Equal(new[] { "2026-09-01" }, values);
    }

    [Fact]
    public async Task Routing_handler_does_not_add_it_to_non_provider_traffic()
    {
        var captured = new CapturingInnerHandler();
        var handler = new ShashuRoutingHandler(
            "sk-trustgate-fake", captured, new ShashuRoutingOptions { BaseUrl = TestGateway.Uri });
        using var http = new HttpClient(handler);

        await http.GetAsync("https://example.com/health");

        Assert.False(captured.LastRequest!.Headers.Contains(Header));
    }

    [Fact]
    public async Task Routing_handler_keeps_a_version_the_caller_set()
    {
        var captured = new CapturingInnerHandler();
        var handler = new ShashuRoutingHandler(
            "sk-trustgate-fake", captured, new ShashuRoutingOptions { BaseUrl = TestGateway.Uri });
        using var http = new HttpClient(handler);
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions");
        req.Headers.TryAddWithoutValidation(Header, "2026-10-01");

        await http.SendAsync(req);

        Assert.True(captured.LastRequest!.Headers.TryGetValues(Header, out var values));
        Assert.Equal(new[] { "2026-10-01" }, values);
    }

    [Fact]
    public async Task Routing_handler_opt_out_sends_no_version()
    {
        var captured = new CapturingInnerHandler();
        var handler = new ShashuRoutingHandler(
            "sk-trustgate-fake", captured,
            new ShashuRoutingOptions { BaseUrl = TestGateway.Uri, ContractVersion = null });
        using var http = new HttpClient(handler);
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions");

        await http.SendAsync(req);

        Assert.False(captured.LastRequest!.Headers.Contains(Header));
    }

    [Fact]
    public async Task A_version_on_the_callers_HttpClient_defaults_wins()
    {
        var (client, seen) = Recording(HttpStatusCode.OK, "{" + Chat200 + "}",
            configure: http => http.DefaultRequestHeaders.TryAddWithoutValidation(Header, "2026-10-01"));
        using (client)
            await client.Chat.Completions.CreateAsync(Request);

        Assert.Equal(new[] { "2026-10-01" }, seen.Values(Header));
    }

    [Fact]
    public async Task No_block_encoding_header_is_sent()
    {
        var (client, seen) = Recording(HttpStatusCode.OK, "{" + Chat200 + "}");
        using (client)
            await client.Chat.Completions.CreateAsync(Request);

        Assert.Empty(seen.Values("shashu-block-encoding"));
    }

    // --- (b) Monitor: a flagged, delivered save ------------------------------

    [Fact]
    public async Task Monitor_flagged_save_is_listed_and_not_counted_as_blocked()
    {
        using var client = Canned(HttpStatusCode.OK, Delivered(MonitorMemory));

        var resp = await client.Chat.Completions.CreateAsync(Request);

        var d = Shashu.DecisionOf(resp);
        Assert.NotNull(d);
        Assert.Equal("allow", d!.Decision);
        Assert.Equal(0, d.MemoryBlocked);
        Assert.Equal(1, d.Memory!.Writes);
        Assert.Equal("body", d.Memory.Carrier);
        var item = Assert.Single(d.Memory.Items);
        Assert.Equal("write", item.Kind);
        Assert.Equal("save_memory", item.Tool);
        Assert.Equal("flagged", item.State);
        Assert.Equal("memory_sink_injection", item.Reason);
        Assert.Equal("gateway", item.Enforcement);
        var detectors = Assert.IsAssignableFrom<IReadOnlyDictionary<string, string>>(d.Inspection!["detectors"]);
        Assert.Equal("checked", detectors["memory"]);
    }

    // --- (c) Block: the save was stripped, the reply delivered ---------------

    [Fact]
    public async Task Block_stripped_save_is_a_normal_reply_with_one_blocked()
    {
        using var client = Canned(HttpStatusCode.OK, Delivered(BlockMemory));

        var resp = await client.Chat.Completions.CreateAsync(Request);

        Assert.Equal("Noted.", resp.Choices[0].Message.Content);
        var d = Shashu.DecisionOf(resp)!;
        Assert.Equal(1, d.MemoryBlocked);
        Assert.Equal("blocked", Assert.Single(d.Memory!.Items).State);
        Assert.False(Shashu.IsBlock(resp));
    }

    // --- (d) refusals arrive as errors ---------------------------------------

    [Fact]
    public async Task Content_block_400_raises_the_content_filter_exception_with_the_sentence()
    {
        using var client = Canned(HttpStatusCode.BadRequest,
            Refusal("content_filter", "jailbreak", InjectionSentence, "block"));

        var ex = await Assert.ThrowsAsync<ShashuContentFilterException>(
            () => client.Chat.Completions.CreateAsync(Request));

        Assert.Equal(400, ex.StatusCode);
        Assert.Equal(InjectionSentence, ex.Message);
        Assert.Equal("jailbreak", ex.Code);
        Assert.True(Shashu.IsBlock(ex));
    }

    [Fact]
    public async Task Memory_refusal_403_raises_MemoryWriteBlocked_with_the_sentence()
    {
        using var client = Canned(HttpStatusCode.Forbidden,
            Refusal("permission_error", "tool_policy", MemorySentence, "refuse", "\"memory\":" + BlockMemory));

        var ex = await Assert.ThrowsAsync<MemoryWriteBlocked>(() => client.Chat.Completions.CreateAsync(Request));

        Assert.Equal(MemorySentence, ex.Message);
        Assert.Equal(1, ex.Memory!.WritesBlocked);
    }

    [Fact]
    public async Task Policy_refusal_403_without_a_memory_block_is_a_policy_violation()
    {
        using var client = Canned(HttpStatusCode.Forbidden,
            Refusal("permission_error", "feature_disabled", "This model is not allowed for this agent.", "refuse"));

        var ex = await Assert.ThrowsAsync<ShashuPolicyViolation>(() => client.Chat.Completions.CreateAsync(Request));

        Assert.IsNotType<MemoryWriteBlocked>(ex);
        Assert.Equal("This model is not allowed for this agent.", ex.Message);
        Assert.Equal("permission_error", ex.ErrorType);
    }

    [Fact]
    public async Task Fail_closed_503_reads_the_sentence_not_the_raw_body()
    {
        const string sentence = "The safety check did not finish in time, so the request was not sent. Try again.";
        using var client = Canned(HttpStatusCode.ServiceUnavailable,
            Refusal("api_error", "inspector_timeout", sentence, "fail"));

        var ex = await Assert.ThrowsAsync<ShashuException>(() => client.Chat.Completions.CreateAsync(Request));

        Assert.Equal(503, ex.StatusCode);
        Assert.Equal(sentence, ex.Message);
        Assert.Contains("inspector_timeout", ex.ResponseBody);
    }

    [Fact]
    public async Task A_non_contract_error_body_keeps_the_raw_snippet()
    {
        using var client = Canned(HttpStatusCode.BadGateway, "upstream exploded");

        var ex = await Assert.ThrowsAsync<ShashuException>(() => client.Chat.Completions.CreateAsync(Request));

        Assert.Equal("Shashu request failed with HTTP 502: upstream exploded", ex.Message);
    }

    // --- (e) streaming refusal ------------------------------------------------

    [Fact]
    public async Task Streaming_content_block_400_raises_before_any_chunk()
    {
        using var client = Canned(HttpStatusCode.BadRequest,
            Refusal("content_filter", "jailbreak", InjectionSentence, "block"));

        var ex = await Assert.ThrowsAsync<ShashuContentFilterException>(async () =>
        {
            await foreach (var _ in client.Chat.Completions.CreateStreamAsync(Request))
            {
            }
        });

        Assert.Equal(400, ex.StatusCode);
        Assert.Equal(InjectionSentence, ex.Message);
    }

    // --- (f) opted out: exactly as before ------------------------------------

    [Fact]
    public async Task Opt_out_sends_no_version_header()
    {
        var (client, seen) = Recording(HttpStatusCode.OK, "{" + Chat200 + "}",
            options: o => o.ContractVersion = null);
        using (client)
            await client.Chat.Completions.CreateAsync(Request);

        Assert.Empty(seen.Values(Header));
    }

    [Fact]
    public async Task Opt_out_legacy_200_block_still_throws_as_before()
    {
        const string legacy =
            "{\"id\":\"c1\",\"object\":\"chat.completion\",\"created\":1,\"model\":\"gpt-4o\",\"choices\":[{\"index\":0,"
            + "\"message\":{\"role\":\"assistant\",\"content\":\"I cannot fulfill this request because it violates "
            + "the organization's security policy.\"},\"finish_reason\":\"content_filter\"}]}";
        var (client, _) = Recording(HttpStatusCode.OK, legacy, options: o => o.ContractVersion = null);
        using (client)
        {
            var ex = await Assert.ThrowsAsync<ShashuContentFilterException>(
                () => client.Chat.Completions.CreateAsync(Request));
            Assert.Equal(200, ex.StatusCode);
            Assert.Equal("content_filter", ex.ErrorType);
            Assert.Contains("security policy", ex.Message);
        }
    }

    // --------------------------------------------------------------------------

    private static ShashuClient Canned(HttpStatusCode status, string body) =>
        UnknownValuesToleratedTests.Client(status, body, new Dictionary<string, string>());

    private static (ShashuClient client, HeaderRecorder seen) Recording(
        HttpStatusCode status, string body, string contentType = "application/json",
        Action<ShashuConfig>? options = null, Action<HttpClient>? configure = null)
    {
        var recorder = new HeaderRecorder(status, body, contentType);
        var http = new HttpClient(recorder) { BaseAddress = TestGateway.Uri };
        configure?.Invoke(http);
        var opts = new ShashuConfig { BaseUrl = TestGateway.Uri, ApiKey = "sk-trustgate-test", MaxRetries = 0 };
        options?.Invoke(opts);
        return (new ShashuClient(opts, http), recorder);
    }

    /// <summary>Records the headers that reached the wire (defaults merged), answers one canned response.</summary>
    private sealed class HeaderRecorder : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;
        private readonly string _contentType;
        private readonly List<KeyValuePair<string, string[]>> _headers = new();

        public HeaderRecorder(HttpStatusCode status, string body, string contentType)
        {
            _status = status;
            _body = body;
            _contentType = contentType;
        }

        public string[] Values(string name) => _headers
            .Where(h => string.Equals(h.Key, name, StringComparison.OrdinalIgnoreCase))
            .SelectMany(h => h.Value)
            .ToArray();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            foreach (var h in request.Headers)
                _headers.Add(new KeyValuePair<string, string[]>(h.Key, h.Value.ToArray()));
            return Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, _contentType),
            });
        }
    }
}
