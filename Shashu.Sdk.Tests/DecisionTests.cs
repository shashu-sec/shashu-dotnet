using Shashu;
using Shashu.Chat;
using Xunit;

namespace Shashu.Tests;

/// <summary>
/// Decision reading. Absence is today's behaviour. An unrecognised
/// <c>decision</c> is a block. No code list.
/// </summary>
public class DecisionTests
{
    [Theory]
    [InlineData("allow")]
    [InlineData("allow_altered")]
    [InlineData("not_checked")]
    [InlineData("block")]
    [InlineData("refuse")]
    [InlineData("fail")]
    public void DecisionFrom_body_surfaces_each_of_the_six(string decision)
    {
        var body = $"{{\"trustgate\":{{\"decision\":\"{decision}\",\"retryable\":false,\"id\":\"i\"}}}}";
        var read = Shashu.DecisionFrom(body, headers: null);
        Assert.NotNull(read);
        Assert.Equal(decision, read!.Decision);
    }

    [Theory]
    [InlineData("allow")]
    [InlineData("allow_altered")]
    [InlineData("not_checked")]
    [InlineData("block")]
    [InlineData("refuse")]
    [InlineData("fail")]
    public void DecisionFrom_headers_surfaces_each_of_the_six(string decision)
    {
        var headers = new Dictionary<string, string>
        {
            ["x-trustgate-decision"] = decision,
        };
        var read = Shashu.DecisionFrom(body: null, headers);
        Assert.NotNull(read);
        Assert.Equal(decision, read!.Decision);
    }

    [Fact]
    public void Absence_returns_nothing()
    {
        Assert.Null(Shashu.DecisionFrom(body: null, headers: null));
        Assert.Null(Shashu.DecisionFrom("{\"id\":\"c1\"}", headers: null));
        Assert.Null(Shashu.DecisionOf(null));
        Assert.Null(Shashu.DecisionOf(new ChatCompletionResponse()));
    }

    [Fact]
    public void Absence_IsBlock_is_false()
    {
        // IsBlock(false) alone cannot distinguish absence from a
        // synthesised allow — both are not-a-block. DecisionOf must
        // also be empty, or the absence branch is unproven.
        Assert.False(Shashu.IsBlock(null));
        Assert.Null(Shashu.DecisionOf(null));
        Assert.False(Shashu.IsBlock(new Exception("unrelated")));
        Assert.Null(Shashu.DecisionOf(new Exception("unrelated")));
        Assert.False(Shashu.IsBlock(new ChatCompletionResponse()));
        Assert.Null(Shashu.DecisionOf(new ChatCompletionResponse()));
    }

    [Theory]
    [InlineData("allow", false)]
    [InlineData("allow_altered", false)]
    [InlineData("not_checked", false)]
    [InlineData("block", true)]
    [InlineData("refuse", false)]
    [InlineData("fail", false)]
    [InlineData("brand_new_this_week", true)]
    public void IsBlock_reads_decision_only_and_fails_closed(string decision, bool blocked)
    {
        var body = $"{{\"trustgate\":{{\"decision\":\"{decision}\"}}}}";
        Assert.Equal(blocked, Shashu.IsBlock(body));
    }

    /// <summary>
    /// THE BLOCK HELPER MEANS <c>block</c>, AND NOTHING ELSE.
    ///
    /// <para><c>IsBlock</c> was <c>!Delivered.Contains(decision)</c>, which
    /// truthfully answers a DIFFERENT question — "did I get content at all" —
    /// and answering it under this name tells a customer refused on policy
    /// that a guardrail fired on their content. A missing API key
    /// (<c>refuse</c> / <c>auth_missing</c>) is not a content block, and a
    /// gateway that broke (<c>fail</c>) decided nothing at all.</para>
    ///
    /// <para>Fail-closed is unchanged and is asserted beside these: an
    /// UNRECOGNISED decision is still a block, because it is coerced to one
    /// before the comparison — the same shape as Python's
    /// <c>_coerce_decision</c>. Narrowing to a bare string equality on the raw
    /// wire value would lose that, which is mutation M-N below.</para>
    /// </summary>
    [Theory]
    [InlineData("refuse", false)]
    [InlineData("fail", false)]
    public void Refuse_and_fail_are_not_content_blocks(string decision, bool blocked)
    {
        var body = $"{{\"trustgate\":{{\"decision\":\"{decision}\"}}}}";
        Assert.Equal(blocked, Shashu.IsBlock(body));
    }

    /// <summary>
    /// An unknown <c>code</c> must not flip fail-closed. The previous
    /// M2b mutation (unknown code → false) stayed green 26/26 because
    /// no case asserted a block while carrying an unknown code.
    /// </summary>
    [Theory]
    [InlineData("brand_new_this_week", "also_brand_new", true)]
    [InlineData("allow", "also_brand_new", false)]
    public void Unknown_code_does_not_override_decision_fail_closed(string decision, string code, bool blocked)
    {
        var body = $"{{\"trustgate\":{{\"decision\":\"{decision}\",\"code\":\"{code}\"}}}}";
        Assert.Equal(blocked, Shashu.IsBlock(body));
    }

    [Fact]
    public void Nested_error_trustgate_is_the_error_carrier()
    {
        var body = "{\"error\":{\"type\":\"content_filter\",\"trustgate\":{\"decision\":\"block\",\"code\":\"jailbreak\"}}}";
        var read = Shashu.DecisionFrom(body, headers: null);
        Assert.Equal("block", read!.Decision);
        Assert.Equal("jailbreak", read.Code);
        Assert.True(Shashu.IsBlock(body));
    }

    [Fact]
    public void Envelope_without_a_readable_decision_fails_closed()
    {
        var body = "{\"trustgate\":{\"retryable\":false}}";
        Assert.Null(Shashu.DecisionFrom(body, headers: null));
        Assert.True(Shashu.IsBlock(body));
    }

    [Fact]
    public void ContentFilterException_without_decision_fails_closed()
    {
        var ex = ShashuContentFilterException.FromStreamChunk();
        Assert.Null(Shashu.DecisionOf(ex));
        Assert.True(Shashu.IsBlock(ex));
    }

    [Fact]
    public void DecisionOf_reads_extension_data_on_a_response()
    {
        var resp = new ChatCompletionResponse
        {
            ExtraFields = new Dictionary<string, System.Text.Json.JsonElement>
            {
                ["trustgate"] = System.Text.Json.JsonDocument.Parse(
                    "{\"decision\":\"allow_altered\",\"code\":\"dlp\"}").RootElement.Clone(),
            },
        };
        var read = Shashu.DecisionOf(resp);
        Assert.Equal("allow_altered", read!.Decision);
        Assert.False(Shashu.IsBlock(resp));
    }

    [Fact]
    public void DecisionOf_reads_headers_when_the_body_has_none()
    {
        var resp = new ChatCompletionResponse
        {
            ShashuHeaders = new Dictionary<string, string>
            {
                ["x-trustgate-decision"] = "refuse",
            },
        };
        var read = Shashu.DecisionOf(resp);
        Assert.Equal("refuse", read!.Decision);
        // The header carrier is read; the decision is a REFUSE, so it is not a
        // content block. This line asserted True until 2026-09-17 and was one
        // of the places the old !Delivered.Contains semantics were written down.
        Assert.False(Shashu.IsBlock(resp));
    }
}
