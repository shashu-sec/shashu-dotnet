using System.Net;
using Shashu.Chat;
using Xunit;

namespace Shashu.Tests;

/// <summary>
/// FILE-06: reading the file, memory and route-policy blocks off a decision.
/// </summary>
/// <remarks>
/// Sibling of the Python SDK's <c>tests/test_file_memory_route.py</c>: the same
/// six wire responses and the same assertions, in C#.
/// </remarks>
public class FileMemoryRouteTests
{
    private const string Chat200 =
        "\"id\":\"chatcmpl-mock\",\"object\":\"chat.completion\",\"created\":0,\"model\":\"mock-model\","
        + "\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"ok\"},\"finish_reason\":\"stop\"}]";

    private const string AttachmentsFlagged =
        "{\"total\":2,\"scanned\":1,\"scanned_partial\":0,\"not_inspected\":1,\"blocked\":0,\"items\":["
        + "{\"index\":0,\"path\":\"messages[0].content[1]\",\"filename\":\"q3.pdf\",\"declared_type\":\"application/pdf\","
        + "\"sniffed_type\":\"application/pdf\",\"sha256\":\"aa11\",\"bytes\":1834021,\"source\":\"inline\","
        + "\"state\":\"scanned\",\"reason\":null,\"findings\":[]},"
        + "{\"index\":1,\"path\":\"messages[0].content[2]\",\"filename\":\"notes.one\",\"declared_type\":\"application/onenote\","
        + "\"sniffed_type\":\"application/onenote\",\"sha256\":\"bb22\",\"bytes\":92110,\"source\":\"inline\","
        + "\"state\":\"not_inspected\",\"reason\":\"unsupported_type\",\"findings\":[]}]}";

    private const string AttachmentsBlocked =
        "{\"total\":1,\"scanned\":0,\"scanned_partial\":0,\"not_inspected\":0,\"blocked\":1,\"items\":["
        + "{\"index\":0,\"path\":\"messages[0].content[1]\",\"filename\":\"a.pdf\",\"declared_type\":\"application/pdf\","
        + "\"sniffed_type\":\"application/x-msdownload\",\"sha256\":\"cc33\",\"bytes\":12,\"source\":\"inline\","
        + "\"state\":\"blocked\",\"reason\":\"type_mismatch\",\"findings\":[{\"detector\":\"file\","
        + "\"category\":\"fair_lending.disparate_treatment\",\"code\":\"file_policy\",\"severity\":\"high\","
        + "\"locator\":\"page[1]\",\"snippet_redacted\":\"[redacted]\"}]}]}";

    private const string MemoryBlocked =
        "{\"coverage\":\"gateway\",\"writes\":1,\"writes_blocked\":1,\"recalls\":0,\"recalls_flagged\":0,\"items\":["
        + "{\"kind\":\"write\",\"tool\":\"memory.write\",\"store\":\"agent\",\"scope\":\"session\",\"source\":\"user\","
        + "\"enforcement\":\"block\",\"memory_ref\":\"m1\",\"seen_before\":false,\"state\":\"blocked\","
        + "\"reason\":\"memory_sink_injection\"}]}";

    private const string RoutePolicyJson =
        "{\"route\":\"general-chat\",\"category\":\"phi\",\"reason\":\"route_not_authorized_for_phi\","
        + "\"requested_route\":\"general-chat\"}";

    private static string Envelope(string decision, string? code, string? blocks) =>
        "{\"decision\":\"" + decision + "\",\"retryable\":false,\"id\":\"b510c203\","
        + "\"inspection\":{\"input\":\"checked\",\"output\":\"checked\"},\"versions\":{\"contract\":\"2026-10-01\"},"
        + "\"message\":\"a sentence a client must not parse\""
        + (code is null ? "" : ",\"code\":\"" + code + "\"")
        + (blocks is null ? "" : "," + blocks) + "}";

    private static Dictionary<string, string> Headers(string decision, string? code, params string[] extra)
    {
        var h = new Dictionary<string, string>
        {
            ["x-request-id"] = "b510c203",
            ["x-trustgate-decision"] = decision,
            ["x-trustgate-inspection"] = "input=checked;output=checked",
            ["x-should-retry"] = "false",
        };
        if (code is not null)
            h["x-trustgate-code"] = code;
        for (var i = 0; i < extra.Length; i += 2)
            h[extra[i]] = extra[i + 1];
        return h;
    }

    private static string Ok(string decision, string? code, string? blocks) =>
        "{" + Chat200 + ",\"trustgate\":" + Envelope(decision, code, blocks) + "}";

    private static string Refused(string code, string message, string blocks) =>
        "{\"error\":{\"message\":\"" + message + "\",\"type\":\"permission_error\",\"param\":null,\"code\":\"" + code
        + "\",\"trustgate\":" + Envelope("refuse", code, blocks) + "},\"choices\":[]}";

    private sealed record Scenario(HttpStatusCode Status, string Body, Dictionary<string, string> Headers);

    /// <summary>The six wire responses. The unit and e2e tests read the same bytes.</summary>
    private static readonly Dictionary<string, Scenario> Scenarios = new()
    {
        ["no_file"] = new(HttpStatusCode.OK, Ok("allow", null, null), Headers("allow", null)),
        ["file_flagged"] = new(HttpStatusCode.OK,
            Ok("not_checked", "attachment_uninspected", "\"attachments\":" + AttachmentsFlagged),
            Headers("not_checked", "attachment_uninspected",
                "x-trustgate-attachments", "total=2;scanned=1;not_inspected=1;blocked=0")),
        ["file_blocked"] = new(HttpStatusCode.Forbidden,
            Refused("file_policy", "The file a.pdf declared one type and its contents matched another, so it was not forwarded.",
                "\"attachments\":" + AttachmentsBlocked),
            Headers("refuse", "file_policy", "x-trustgate-attachments", "total=1;scanned=0;not_inspected=0;blocked=1")),
        ["memory_blocked"] = new(HttpStatusCode.Forbidden,
            Refused("tool_policy", "A save through memory.write contained hidden instructions, so it was not stored.",
                "\"memory\":" + MemoryBlocked),
            Headers("refuse", "tool_policy", "x-trustgate-memory", "writes=1;blocked=1;recalls=0;flagged=0")),
        ["route_blocked"] = new(HttpStatusCode.Forbidden,
            Refused("route_data_policy", "This request contains patient health information and route general-chat "
                + "is not approved for it. Use a BAA-covered route.", "\"route_policy\":" + RoutePolicyJson),
            Headers("refuse", "route_data_policy")),
        // A streaming / header-only reader: no envelope in the body, counts in headers.
        ["header_only"] = new(HttpStatusCode.OK, "{" + Chat200 + "}",
            Headers("not_checked", "attachment_uninspected",
                "x-trustgate-attachments", "total=3;scanned=1;not_inspected=1;blocked=0",
                "x-trustgate-memory", "writes=2;blocked=0;recalls=4;flagged=1")),
    };

    private static ShashuDecision Read(string name)
    {
        var s = Scenarios[name];
        var d = Shashu.DecisionFrom(s.Body, s.Headers);
        Assert.NotNull(d);
        return d!;
    }

    // --- attachments ---------------------------------------------------------

    [Fact]
    public void Attachments_no_file_has_none_and_zero_counts()
    {
        var d = Read("no_file");
        Assert.Null(d.Attachments);
        Assert.Equal(0, d.FilesUnverified);
        Assert.Equal(0, d.FilesBlocked);
    }

    [Fact]
    public void Restore_key_is_readable_from_trustgate_extra_fields()
    {
        const string attachments =
            "{\"total\":1,\"scanned\":1,\"scanned_partial\":0,\"not_inspected\":0,\"blocked\":0,"
            + "\"items\":[{\"filename\":\"a.pdf\",\"note\":\"This PDF contains placeholders such as <PERSON_1> "
            + "where protected data was hidden from the model. Trustgate AI can't put the real values back "
            + "inside this PDF, so they are listed with this file.\","
            + "\"key\":[{\"placeholder\":\"<email_redacted_1>\",\"value\":\"alice@corp.example\"}]}]}";
        var body = Ok("allow", null, "\"attachments\":" + attachments);
        var d = Shashu.DecisionFrom(body, headers: null);
        Assert.NotNull(d!.Attachments);
        Assert.Equal("a.pdf", d.Attachments.Items[0].Filename);
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        var item = doc.RootElement.GetProperty("trustgate").GetProperty("attachments").GetProperty("items")[0];
        Assert.Contains("This PDF contains placeholders", item.GetProperty("note").GetString());
        Assert.Equal("alice@corp.example", item.GetProperty("key")[0].GetProperty("value").GetString());
    }

    [Fact]
    public void Attachments_flagged_file_is_read_off_the_body()
    {
        var d = Read("file_flagged");
        var att = d.Attachments!;
        Assert.Equal("body", att.Carrier);
        Assert.Equal(new[] { 2, 1, 0, 1, 0 }, new[] { att.Total, att.Scanned, att.ScannedPartial, att.NotInspected, att.Blocked });
        Assert.Equal(1, d.FilesUnverified);
        var first = att.Items[0];
        var second = att.Items[1];
        Assert.Equal("q3.pdf", first.Filename);
        Assert.Equal(AttachmentState.Scanned, first.State);
        Assert.Null(first.Reason);
        Assert.Equal(1834021L, first.Bytes);
        Assert.Equal("aa11", first.Sha256);
        Assert.Equal("inline", first.Source);
        Assert.Equal(AttachmentState.NotInspected, second.State);
        Assert.Equal("not_inspected", second.RawState);
        Assert.Equal("unsupported_type", second.Reason);
        Assert.Equal("application/onenote", second.DeclaredType);
        Assert.Equal("application/onenote", second.SniffedType);
    }

    [Fact]
    public void Attachments_blocked_file_and_its_findings()
    {
        var d = Read("file_blocked");
        Assert.Equal(1, d.FilesBlocked);
        var item = d.Attachments!.Items[0];
        Assert.Equal(AttachmentState.Blocked, item.State);
        Assert.Equal("type_mismatch", item.Reason);
        var f = item.Findings[0];
        Assert.Equal("file", f.Detector);
        Assert.Equal("fair_lending.disparate_treatment", f.Category);
        Assert.Equal("file_policy", f.Code);
        Assert.Equal("high", f.Severity);
        Assert.Equal("page[1]", f.Locator);
    }

    [Fact]
    public void Attachments_header_only_falls_back_to_the_counts_and_says_so()
    {
        var d = Read("header_only");
        var att = d.Attachments!;
        Assert.Equal("header", att.Carrier);
        Assert.Empty(att.Items);
        Assert.Equal(new[] { 3, 1, 1, 0 }, new[] { att.Total, att.Scanned, att.NotInspected, att.Blocked });
        Assert.Equal(1, att.ScannedPartial);
        Assert.Equal(2, d.FilesUnverified);
    }

    [Fact]
    public void Attachments_the_body_wins_over_the_header()
    {
        var s = Scenarios["file_flagged"];
        var h = new Dictionary<string, string>(s.Headers)
        {
            ["x-trustgate-attachments"] = "total=9;scanned=9;not_inspected=0;blocked=0",
        };
        var d = Shashu.DecisionFrom(s.Body, h)!;
        Assert.Equal("body", d.Attachments!.Carrier);
        Assert.Equal(2, d.Attachments.Total);
    }

    [Fact]
    public void Attachments_the_items_are_read_only()
    {
        var items = Read("file_flagged").Attachments!.Items;
        Assert.Throws<NotSupportedException>(() => ((IList<Attachment>)items).Clear());
    }

    // --- memory ----------------------------------------------------------------

    [Fact]
    public void Memory_none_on_a_plain_response()
    {
        var d = Read("no_file");
        Assert.Null(d.Memory);
        Assert.Equal(0, d.MemoryBlocked);
        Assert.Equal(0, d.MemoryRecallsFlagged);
        Assert.Null(d.MemoryCoverage);
    }

    [Fact]
    public void Memory_blocked_write_is_read_off_the_body()
    {
        var d = Read("memory_blocked");
        var mem = d.Memory!;
        Assert.Equal("body", mem.Carrier);
        Assert.Equal(new[] { 1, 1, 0, 0 }, new[] { mem.Writes, mem.WritesBlocked, mem.Recalls, mem.RecallsFlagged });
        Assert.Equal(1, d.MemoryBlocked);
        Assert.Equal("gateway", d.MemoryCoverage);
        var item = mem.Items[0];
        Assert.Equal("write", item.Kind);
        Assert.Equal("memory.write", item.Tool);
        Assert.Equal("agent", item.Store);
        Assert.Equal("session", item.Scope);
        Assert.Equal("user", item.Source);
        Assert.Equal("block", item.Enforcement);
        Assert.Equal("m1", item.MemoryRef);
        Assert.False(item.SeenBefore);
        Assert.Equal("blocked", item.State);
        Assert.Equal("memory_sink_injection", item.Reason);
    }

    [Fact]
    public void Memory_header_only_counts()
    {
        var d = Read("header_only");
        var mem = d.Memory!;
        Assert.Equal("header", mem.Carrier);
        Assert.Empty(mem.Items);
        Assert.Equal(new[] { 2, 0, 4, 1 }, new[] { mem.Writes, mem.WritesBlocked, mem.Recalls, mem.RecallsFlagged });
        Assert.Equal(1, d.MemoryRecallsFlagged);
        Assert.Null(d.MemoryCoverage);
    }

    // --- route policy ----------------------------------------------------------

    [Fact]
    public void RoutePolicy_absent_on_a_plain_response() => Assert.Null(Read("no_file").RoutePolicy);

    [Fact]
    public void RoutePolicy_read_off_the_body()
    {
        var rp = Read("route_blocked").RoutePolicy!;
        Assert.Equal("general-chat", rp.Route);
        Assert.Equal("phi", rp.Category);
        Assert.Equal("route_not_authorized_for_phi", rp.Reason);
        Assert.Equal("general-chat", rp.RequestedRoute);
        Assert.Null(rp.DegradedRoute);
    }

    // --- tolerance ---------------------------------------------------------------

    [Fact]
    public void Tolerance_unknown_code_reason_field_and_state_are_read_not_thrown()
    {
        var att = AttachmentsFlagged
            .Replace("\"state\":\"scanned\"", "\"state\":\"quarantined_zz\",\"made_up_item_field\":[1,2]")
            .Replace("\"reason\":null,\"findings\":[]",
                "\"reason\":\"made_up_reason_zz\",\"findings\":[{\"detector\":\"zz\",\"category\":\"zz.new\",\"made_up\":true}]")
            .Replace("\"total\":2", "\"made_up_block_field\":{\"x\":1},\"total\":2");
        var mem = MemoryBlocked
            .Replace("\"gateway\"", "\"made_up_coverage_zz\"")
            .Replace("\"memory_sink_injection\"", "\"made_up_memory_reason_zz\",\"made_up_item_field\":\"x\"")
            .Replace("\"kind\":\"write\"", "\"kind\":\"made_up_kind_zz\"");
        var route = RoutePolicyJson.Replace("route_not_authorized_for_phi", "made_up_route_reason_zz")
            .Replace("{", "{\"made_up_field\":7,");
        var env = Envelope("refuse", "made_up_code_zz",
            "\"attachments\":" + att + ",\"memory\":" + mem + ",\"route_policy\":" + route
            + ",\"made_up_top_level_field\":{\"nested\":[\"x\"]}");
        var d = Shashu.DecisionFrom("{\"error\":{\"message\":\"m\",\"trustgate\":" + env + "}}",
            Headers("refuse", "made_up_code_zz",
                "x-trustgate-attachments", "total=2;scanned=1;not_inspected=1;blocked=0;made_up=4",
                "x-trustgate-memory", "writes=1;blocked=1;made_up=9"))!;
        Assert.Equal("made_up_code_zz", d.Code);
        var item = d.Attachments!.Items[0];
        Assert.Equal(AttachmentState.Unknown, item.State);
        Assert.Equal("quarantined_zz", item.RawState);
        Assert.Equal("made_up_reason_zz", item.Reason);
        Assert.Equal("zz.new", item.Findings[0].Category);
        Assert.Equal("made_up_memory_reason_zz", d.Memory!.Items[0].Reason);
        Assert.Equal("made_up_kind_zz", d.Memory.Items[0].Kind);
        Assert.Equal("made_up_coverage_zz", d.MemoryCoverage);
        Assert.Equal("made_up_route_reason_zz", d.RoutePolicy!.Reason);
    }

    [Fact]
    public void Tolerance_wrongly_typed_blocks_are_ignored_not_thrown()
    {
        var d = Shashu.DecisionFrom("{\"trustgate\":" + Envelope("allow", null,
            "\"attachments\":\"x\",\"memory\":[1],\"route_policy\":5") + "}", null)!;
        Assert.Null(d.Attachments);
        Assert.Null(d.Memory);
        Assert.Null(d.RoutePolicy);
        d = Shashu.DecisionFrom("{\"trustgate\":" + Envelope("allow", null,
            "\"attachments\":{\"total\":\"two\",\"items\":[\"x\",null,{\"state\":3}]}") + "}", null)!;
        Assert.Equal(0, d.Attachments!.Total);
        Assert.Single(d.Attachments.Items);
        Assert.Equal(AttachmentState.Unknown, d.Attachments.Items[0].State);
        Assert.Null(d.Attachments.Items[0].RawState);
    }

    [Fact]
    public void Tolerance_garbage_count_headers_are_ignored()
    {
        var d = Shashu.DecisionFrom(null, Headers("allow", null,
            "x-trustgate-attachments", "total=;scanned=x;;=3",
            "x-trustgate-memory", "nonsense"))!;
        Assert.Equal("allow", d.Decision);
        Assert.Null(d.Attachments);
        Assert.Null(d.Memory);
    }

    // --- exceptions and IsBlock -----------------------------------------------------

    [Fact]
    public void MemoryWriteBlocked_is_a_policy_violation() =>
        Assert.True(typeof(ShashuPolicyViolation).IsAssignableFrom(typeof(MemoryWriteBlocked)));

    [Theory]
    [InlineData("file_blocked")]
    [InlineData("memory_blocked")]
    [InlineData("route_blocked")]
    public void IsBlock_is_unchanged_for_the_new_refusals(string name) =>
        Assert.False(Shashu.IsBlock(Scenarios[name].Body));

    // --- wire e2e: the six responses through the real client ----------------------------

    private static readonly ChatCompletionRequest Request = new()
    {
        Model = "gpt-4o",
        Messages = new[] { new ChatMessage("user", "hi") },
    };

    private static ShashuClient Serve(string name)
    {
        var s = Scenarios[name];
        return UnknownValuesToleratedTests.Client(s.Status, s.Body, s.Headers);
    }

    private static async Task<ShashuException> Thrown(string name)
    {
        using var client = Serve(name);
        return await Assert.ThrowsAnyAsync<ShashuException>(() => client.Chat.Completions.CreateAsync(Request));
    }

    [Fact]
    public async Task Wire_no_file()
    {
        using var client = Serve("no_file");
        var d = Shashu.DecisionOf(await client.Chat.Completions.CreateAsync(Request))!;
        Assert.Equal("allow", d.Decision);
        Assert.Null(d.Attachments);
        Assert.Null(d.Memory);
        Assert.Null(d.RoutePolicy);
        Assert.Equal(0, d.FilesUnverified + d.FilesBlocked + d.MemoryBlocked);
    }

    [Fact]
    public async Task Wire_file_flagged()
    {
        using var client = Serve("file_flagged");
        var d = Shashu.DecisionOf(await client.Chat.Completions.CreateAsync(Request))!;
        Assert.Equal("not_checked", d.Decision);
        Assert.Equal("body", d.Attachments!.Carrier);
        Assert.Equal(1, d.FilesUnverified);
        Assert.Equal(new[] { AttachmentState.Scanned, AttachmentState.NotInspected }, d.Attachments.Items.Select(i => i.State));
    }

    [Fact]
    public async Task Wire_file_blocked()
    {
        var ex = await Thrown("file_blocked");
        Assert.IsAssignableFrom<ShashuPolicyViolation>(ex);
        Assert.IsNotType<MemoryWriteBlocked>(ex);
        var d = Shashu.DecisionOf(ex)!;
        Assert.Equal(1, d.FilesBlocked);
        Assert.Equal("type_mismatch", d.Attachments!.Items[0].Reason);
        Assert.False(Shashu.IsBlock(ex));
    }

    [Fact]
    public async Task Wire_memory_blocked()
    {
        var ex = await Thrown("memory_blocked");
        var mwb = Assert.IsType<MemoryWriteBlocked>(ex);
        Assert.IsAssignableFrom<ShashuPolicyViolation>(ex);
        Assert.Equal(403, ex.StatusCode);
        Assert.Equal("memory_sink_injection", mwb.Memory!.Items[0].Reason);
        Assert.False(Shashu.IsBlock(ex));
    }

    [Fact]
    public async Task Wire_route_data_policy()
    {
        var ex = await Thrown("route_blocked");
        Assert.IsAssignableFrom<ShashuPolicyViolation>(ex);
        Assert.IsNotType<MemoryWriteBlocked>(ex);
        Assert.Equal("general-chat", ex.RoutePolicy!.Route);
        Assert.Equal("phi", ex.RoutePolicy.Category);
        Assert.False(Shashu.IsBlock(ex));
    }

    [Fact]
    public async Task Wire_header_only()
    {
        using var client = Serve("header_only");
        var d = Shashu.DecisionOf(await client.Chat.Completions.CreateAsync(Request))!;
        Assert.Equal("header", d.Attachments!.Carrier);
        Assert.Equal("header", d.Memory!.Carrier);
        Assert.Equal(2, d.FilesUnverified);
        Assert.Equal(1, d.MemoryRecallsFlagged);
    }
}
