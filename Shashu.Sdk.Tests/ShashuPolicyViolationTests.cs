using System.Net;
using System.Text;
using Shashu;
using Shashu.Chat;
using Xunit;
using Shashu.Tests.Harness;

namespace Shashu.Tests;

/// <summary>
/// Verifies 403 responses surface as the typed
/// <see cref="ShashuPolicyViolation"/> with the gateway's
/// <c>error.message</c> parsed and exposed via <c>.Message</c>.
/// </summary>
/// <remarks>
/// <para><b>Every body in this file is a verbatim capture from a live gateway
/// (2026-09-01, <c>sdks/e2e/capture_error_contract.py</c>).</b> That is a rule,
/// not a courtesy: <see cref="FixedResponseHandler"/> returns whatever it is
/// handed and will pass against a body the gateway has never sent. It did
/// exactly that for months — it asserted <c>error.type == "policy_violation"</c>,
/// a value no gateway code path produces, and stayed green the whole time.
/// Re-capture before changing a body here; do not hand-write one.</para>
/// </remarks>
public class ShashuPolicyViolationTests
{
    /// <summary>
    /// Captured: <c>POST /v1/chat/completions</c>, model outside the agent's
    /// allowed routes. <c>type</c> is <c>invalid_request_error</c> — the same
    /// value a 401 and a 422 carry — and <c>choices: []</c> is present so naive
    /// OpenAI clients do not crash mapping over the error path.
    /// </summary>
    private const string AccessDenied403 =
        "{\"error\":{\"message\":\"Access Denied: Model 'claude-3-opus-20240229' is not in the "
        + "allowed routes for this agent.\",\"type\":\"invalid_request_error\",\"param\":null,"
        + "\"code\":\"403\"},\"choices\":[]}";

    /// <summary>
    /// Captured: the same decision on <c>/v1/messages</c> with
    /// <c>TRUSTGATE_WIRE_SHAPE_MESSAGES=enforce</c>. The per-wire adapter
    /// rewrites rendered error envelopes into Anthropic's shape.
    /// </summary>
    private const string AccessDenied403MessagesWire =
        "{\"type\":\"error\",\"error\":{\"type\":\"permission_error\",\"message\":\"Access Denied: "
        + "Model 'claude-3-opus-20240229' is not in the allowed routes for this agent.\"}}";

    private const string DeniedMessage =
        "Access Denied: Model 'claude-3-opus-20240229' is not in the allowed routes for this agent.";

    [Fact]
    public async Task Access_denial_403_throws_ShashuPolicyViolation_with_parsed_message()
    {
        var ex = await Post403(AccessDenied403);

        Assert.Equal(403, ex.StatusCode);
        Assert.Equal(DeniedMessage, ex.Message);
        Assert.Equal(AccessDenied403, ex.ResponseBody);
    }

    [Fact]
    public async Task Error_type_on_a_gateway_403_is_invalid_request_error_not_a_policy_discriminator()
    {
        // Pinning the value the gateway ACTUALLY sends. `invalid_request_error`
        // is what every gateway-raised 4xx carries, which is precisely why
        // callers must branch on StatusCode and not on ErrorType.
        var ex = await Post403(AccessDenied403);

        Assert.Equal("invalid_request_error", ex.ErrorType);
    }

    [Fact]
    public async Task Messages_wire_403_is_anthropic_shaped_and_still_parses()
    {
        var ex = await Post403(AccessDenied403MessagesWire);

        Assert.Equal(DeniedMessage, ex.Message);
        Assert.Equal("permission_error", ex.ErrorType);
    }

    [Fact]
    public async Task Catch_ShashuException_parent_also_works()
    {
        // Customer might write `catch (ShashuException ex)` for generic
        // handling; the typed policy-violation must still match.
        var ex = await Post403(AccessDenied403);

        Assert.IsAssignableFrom<ShashuException>(ex);
    }

    [Fact]
    public async Task Unparseable_body_uses_default_message()
    {
        // Defensive parser coverage, NOT a gateway contract: the gateway has
        // never served a non-JSON body on this path. A reverse proxy in front
        // of it can, which is the only reason this branch exists.
        var ex = await Post403("<html>plain text not json</html>");

        Assert.Equal("Shashu policy violation (403)", ex.Message);
        Assert.Contains("plain text not json", ex.ResponseBody);
    }

    [Fact]
    public async Task Bare_top_level_message_falls_back_correctly()
    {
        // Also defensive parser coverage. This file used to describe
        // {"message": "..."} as what "some legacy gateway versions return".
        // No released gateway has ever returned it — the envelope has carried
        // `error.message` since the OpenAI-compatible error shape landed.
        var ex = await Post403("{\"message\":\"blocked by DLP\"}");

        Assert.Equal("blocked by DLP", ex.Message);
    }

    [Fact]
    public async Task Provider_5xx_throws_generic_ShashuException_not_PolicyViolation()
    {
        // Captured shape of an upstream failure: the gateway wraps the provider
        // error in the OpenAI envelope and puts the provider-error class name
        // in `type`, with the message prefixed "SHASHU Provider Error:".
        using var client = BuildClient(
            "{\"error\":{\"message\":\"SHASHU Provider Error: upstream provider down\","
            + "\"type\":\"ProviderServerError\",\"param\":null,\"code\":\"502\"},\"choices\":[]}",
            HttpStatusCode.BadGateway);

        var ex = await Assert.ThrowsAsync<ShashuException>(() =>
            client.Chat.Completions.CreateAsync(new ChatCompletionRequest
            {
                Model = "gpt-4o",
                Messages = new[] { new ChatMessage("user", "x") },
            }));

        Assert.IsNotType<ShashuPolicyViolation>(ex);
        Assert.Equal(502, ex.StatusCode);
    }

    [Fact(Skip = "Asserts an UNIMPLEMENTED contract: the 403 discriminator ticket "
        + "(the gateway issue tracker). "
        + "The gateway has never emitted error.type=\"policy_violation\" — every 4xx it raises "
        + "itself carries \"invalid_request_error\" (proxy/helpers.py::_openai_error_response and "
        + "main.py::http_exception_handler). This test passed only because FixedResponseHandler "
        + "served the body the test itself wrote. Re-enable when the gateway ships a real 403 "
        + "discriminator, and not before.")]
    public async Task Future_gateway_403_carries_a_policy_discriminator_in_error_type()
    {
        const string body = "{\"error\":{\"message\":\"Tool call rejected: prompt-injection pattern "
            + "matched\",\"type\":\"policy_violation\"}}";

        var ex = await Post403(body);

        Assert.Equal("policy_violation", ex.ErrorType);
    }

    private static async Task<ShashuPolicyViolation> Post403(string body)
    {
        using var client = BuildClient(body, HttpStatusCode.Forbidden);
        return await Assert.ThrowsAsync<ShashuPolicyViolation>(() =>
            client.Chat.Completions.CreateAsync(new ChatCompletionRequest
            {
                Model = "claude-3-opus-20240229",
                Messages = new[] { new ChatMessage("user", "hi") },
            }));
    }

    private static ShashuClient BuildClient(string body, HttpStatusCode status)
    {
        var handler = new FixedResponseHandler(body, status);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://test.invalid/") };
        return new ShashuClient(
            new ShashuConfig { BaseUrl = TestGateway.Uri, ApiKey = "sk-shashu-test", MaxRetries = 0 }, http);
    }
}

/// <summary>Always returns the configured body + status, no retries needed.</summary>
internal sealed class FixedResponseHandler : HttpMessageHandler
{
    private readonly string _body;
    private readonly HttpStatusCode _status;

    public FixedResponseHandler(string body, HttpStatusCode status)
    {
        _body = body;
        _status = status;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        return Task.FromResult(new HttpResponseMessage(_status)
        {
            Content = new StringContent(_body, Encoding.UTF8, "application/json"),
        });
    }
}
