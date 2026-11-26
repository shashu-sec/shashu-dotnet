using System.Net;
using System.Text;
using Shashu;
using Shashu.Chat;
using Xunit;
using Shashu.Tests.Harness;

namespace Shashu.Tests;

/// <summary>
/// The five error-dispatch properties, pinned.
/// </summary>
/// <remarks>
/// <para><b>The defect.</b> Under the gateway's <c>explicit</c> block encoding
/// (the default at contract version <c>2026-09-01</c>) a DLP, jailbreak or
/// prompt-injection block arrives as <b>HTTP 400</b> with
/// <c>error.type == "content_filter"</c>. This SDK reported it as a bare
/// <see cref="ShashuException"/> reading "Shashu request failed with HTTP
/// 400" — indistinguishable from a malformed request. A caller with correct
/// code was sent to fix a payload that was never the problem, and the block
/// never reached whatever handles blocks.</para>
///
/// <para><b>The other four</b> are not new behaviour and are asserted anyway:
/// they are the fail-closed properties of <c>ShashuException.FromResponse</c>,
/// which had no test of its own, and they are what stops the fix above from
/// being widened into a swallow. (a) is the fix; (b) an unrecognised code on a
/// 403 stays a policy violation; (c) an unrecognised code on a 400 stays a
/// client error; (d) an absent code dispatches on the transport status; (e) a
/// numeric-looking code is never read as an HTTP status.</para>
///
/// <para><b>Body provenance.</b> The 400 content-block bodies were produced on
/// 2026-09-07 by driving <c>app.contract.responses.build_block</c> in-process
/// from the trustgate-ai checkout — the same function the gateway's <c>/v1</c>
/// hot path calls to serialise a block. Nothing here is hand-written, and the
/// difference from a live capture is stated rather than blurred: an in-process
/// capture proves what the builder emits, not that the hot path reaches it.</para>
/// </remarks>
public class ShashuContentFilterExceptionTests
{
    /// <summary>The <c>explicit</c> encoding, <c>jailbreak</c> finding. HTTP 400.</summary>
    private const string Block400Jailbreak =
        "{\"error\":{\"message\":\"I cannot fulfill this request because it violates the "
        + "organization's security policy.\",\"type\":\"content_filter\",\"param\":null,"
        + "\"code\":\"jailbreak\",\"shashu\":{\"decision\":\"block\",\"retryable\":false,"
        + "\"id\":\"9f4c1c2e-6b3d-4a51-9c0e-2a7f8b1d4e30\",\"code\":\"jailbreak\"}},"
        + "\"choices\":[]}";

    /// <summary>The same encoding, <c>dlp</c> finding — the code varies, the type does not.</summary>
    private const string Block400Dlp =
        "{\"error\":{\"message\":\"I cannot fulfill this request because it violates the "
        + "organization's security policy.\",\"type\":\"content_filter\",\"param\":null,"
        + "\"code\":\"dlp\",\"shashu\":{\"decision\":\"block\",\"retryable\":false,"
        + "\"id\":\"9f4c1c2e-6b3d-4a51-9c0e-2a7f8b1d4e30\",\"code\":\"dlp\"}},\"choices\":[]}";

    /// <summary>
    /// A policy refusal on the contract version. <c>error.code</c> is a §3
    /// REASON, not a status — the case that must still land on
    /// <see cref="ShashuPolicyViolation"/>.
    /// </summary>
    private const string Policy403EgressDenied =
        "{\"error\":{\"message\":\"A2A cross-tenant call blocked: the calling agent belongs to "
        + "a different tenant and cross-tenant calls are not allowed.\","
        + "\"type\":\"permission_error\",\"param\":null,\"code\":\"egress_denied\"},"
        + "\"choices\":[]}";

    /// <summary>
    /// A schema-invalid body on the LLM wire. <c>error.code</c> is
    /// <c>"invalid_request"</c> — neither a §3 reason nor a status, the third
    /// thing the field can hold.
    /// </summary>
    private const string SchemaInvalid400 =
        "{\"error\":{\"message\":\"The request body is not valid for this endpoint.\","
        + "\"type\":\"invalid_request_error\",\"param\":null,\"code\":\"invalid_request\"},"
        + "\"choices\":[],\"detail\":[]}";

    /// <summary>The 500 crash handler: <c>error</c> is a STRING, so there is no error.code.</summary>
    private const string Crash500 =
        "{\"error\":\"Internal Server Error\",\"detail\":\"Internal error\","
        + "\"request_id\":\"dfeffdc1-aedd-4759-b160-dd85fd4cb0a0\"}";

    private const string RefusalMessage =
        "I cannot fulfill this request because it violates the organization's security policy.";

    // --- (a) THE defect ---------------------------------------------------- //

    [Fact]
    public async Task A_400_content_filter_is_a_content_filter_not_a_generic_client_error()
    {
        var ex = await Post<ShashuContentFilterException>(Block400Jailbreak, HttpStatusCode.BadRequest);

        Assert.Equal(400, ex.StatusCode);
        Assert.Equal("content_filter", ex.ErrorType);
        // The finding survives to the caller — the field that says WHICH
        // detector refused, and the reason error.code is no longer a status.
        Assert.Equal("jailbreak", ex.Code);
        // The refusal sentence, not "Shashu request failed with HTTP 400".
        Assert.Equal(RefusalMessage, ex.Message);
    }

    [Fact]
    public async Task A_every_content_finding_lands_on_the_same_type_not_just_jailbreak()
    {
        var ex = await Post<ShashuContentFilterException>(Block400Dlp, HttpStatusCode.BadRequest);

        Assert.Equal("dlp", ex.Code);
    }

    [Fact]
    public async Task A_content_filter_block_is_still_catchable_as_the_parent()
    {
        // A customer writing `catch (ShashuException ex)` for generic
        // handling must keep working.
        var ex = await Post<ShashuException>(Block400Jailbreak, HttpStatusCode.BadRequest);

        Assert.IsType<ShashuContentFilterException>(ex);
    }

    // --- (b) fail closed on an unrecognised 403 code ----------------------- //

    [Fact]
    public async Task B_403_with_an_unrecognised_code_is_still_a_policy_violation()
    {
        var ex = await Post<ShashuException>(Policy403EgressDenied, HttpStatusCode.Forbidden);

        // `egress_denied` is a §3 reason. Nothing may read it as a status, and
        // the 403 branch must not become reachable only when the code is "403".
        var violation = Assert.IsType<ShashuPolicyViolation>(ex);
        Assert.Equal(403, violation.StatusCode);
        Assert.Equal("permission_error", violation.ErrorType);
    }

    // --- (c) an unrecognised 400 code is not swallowed --------------------- //

    [Fact]
    public async Task C_400_with_an_unrecognised_code_stays_a_client_error()
    {
        // Emphatically NOT a content filter: this really is a malformed body,
        // and reporting it as a block would be the mirror image of the defect
        // this file exists to close.
        var ex = await Post<ShashuException>(SchemaInvalid400, HttpStatusCode.BadRequest);

        Assert.IsNotType<ShashuContentFilterException>(ex);
        Assert.Equal(400, ex.StatusCode);
    }

    [Fact]
    public async Task C_400_with_an_unparseable_body_stays_a_client_error()
    {
        var ex = await Post<ShashuException>("not json at all", HttpStatusCode.BadRequest);

        Assert.IsNotType<ShashuContentFilterException>(ex);
        Assert.Equal(400, ex.StatusCode);
    }

    // --- (d) an absent code dispatches on the transport status ------------- //

    [Fact]
    public async Task D_a_code_that_is_absent_entirely_dispatches_on_the_transport_status()
    {
        // The 500 crash handler sends `error` as a bare STRING — no
        // error.code, no error.message.
        var ex = await Post<ShashuException>(Crash500, HttpStatusCode.InternalServerError);

        Assert.Equal(500, ex.StatusCode);
        Assert.IsType<ShashuException>(ex);
    }

    // --- (e) a numeric-looking code is never read as a status -------------- //

    [Theory]
    [InlineData("4_0_3")]
    [InlineData("403_policy")]
    [InlineData("403")]
    [InlineData("0403")]
    public async Task E_a_numeric_looking_code_is_not_dispatched_as_an_http_status(string numericLooking)
    {
        // No §3 code is numeric today, so this is a guard rather than a bug
        // report — but it is the guard that keeps dispatch on the transport
        // status structural. A parser that read `error.code` leniently would
        // turn "4_0_3" or "403_policy" on a 400 into a policy violation nobody
        // raised; this SDK never reads the field for routing, and that is
        // asserted here rather than left to be re-derived.
        var body = Block400Jailbreak.Replace(
            "\"code\":\"jailbreak\"", $"\"code\":\"{numericLooking}\"");

        var ex = await Post<ShashuContentFilterException>(body, HttpStatusCode.BadRequest);

        Assert.Equal(400, ex.StatusCode);
    }

    private static async Task<T> Post<T>(string body, HttpStatusCode status) where T : ShashuException
    {
        // HttpClient owns the handler and disposes it; mirrors BuildClient in
        // ShashuPolicyViolationTests.
        var handler = new FixedResponseHandler(body, status);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://test.invalid/") };
        using var client = new ShashuClient(
            new ShashuConfig
            {
                // There is no default gateway address; an unset one throws
                // ShashuConfigException rather than guessing a host.
                BaseUrl = TestGateway.Uri,
                ApiKey = "sk-shashu-test",
                MaxRetries = 0,
            },
            http);

        return await Assert.ThrowsAnyAsync<T>(() =>
            client.Chat.Completions.CreateAsync(new ChatCompletionRequest
            {
                Model = "gpt-4o",
                Messages = new[] { new ChatMessage("user", "hi") },
            }));
    }
}
