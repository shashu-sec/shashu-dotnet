using Xunit;

namespace Shashu.Tests;

/// <summary>
/// Reading <c>inspection</c> off a decision, <c>detectors.memory</c> included.
/// </summary>
/// <remarks>
/// Sibling of the Python SDK's <c>Decision.inspection</c> and the Node SDK's
/// <c>decision.inspection</c>: the same shape — <c>input</c>, <c>output</c>,
/// <c>reason</c> and a <c>detectors</c> map — whether it was read off the body
/// or off the <c>x-trustgate-inspection</c> header.
/// </remarks>
public class InspectionTests
{
    private static string Body(string? inspection) =>
        "{\"trustgate\":{\"decision\":\"allow\",\"id\":\"r1\""
        + (inspection is null ? "" : ",\"inspection\":" + inspection) + "}}";

    private static string? MemoryState(ShashuDecision d) =>
        d.Inspection!.TryGetValue("detectors", out var det)
            ? ((IReadOnlyDictionary<string, string>)det).GetValueOrDefault("memory")
            : null;

    [Theory]
    [InlineData("checked")]
    [InlineData("partial")]
    [InlineData("skipped")]
    public void Body_memory_detector_is_readable_for_each_state(string state)
    {
        var d = Shashu.DecisionFrom(Body(
            "{\"input\":\"checked\",\"output\":\"checked\",\"detectors\":{\"memory\":\"" + state + "\"}}"), headers: null)!;
        Assert.Equal(state, MemoryState(d));
        Assert.Equal("checked", d.Inspection!["input"]);
    }

    [Fact]
    public void Header_memory_detector_is_collected_under_detectors()
    {
        var d = Shashu.DecisionFrom(body: null, new Dictionary<string, string>
        {
            ["x-trustgate-decision"] = "allow",
            ["x-trustgate-inspection"] = "input=checked;output=skipped;reason=adapter_unreachable;memory=partial",
        })!;
        Assert.Equal("partial", MemoryState(d));
        Assert.Equal("skipped", d.Inspection!["output"]);
        Assert.Equal("adapter_unreachable", d.Inspection["reason"]);
    }

    [Fact]
    public void Absent_inspection_is_null_and_no_detectors_is_null_memory()
    {
        Assert.Null(Shashu.DecisionFrom(Body(null), headers: null)!.Inspection);
        Assert.Null(MemoryState(Shashu.DecisionFrom(
            Body("{\"input\":\"checked\",\"output\":\"checked\"}"), headers: null)!));
    }

    [Fact]
    public void Unknown_states_pass_through_and_non_text_states_are_dropped()
    {
        var d = Shashu.DecisionFrom(Body(
            "{\"input\":\"checked\",\"output\":\"checked\",\"detectors\":{\"memory\":\"brand_new\",\"files\":7}}"),
            headers: null)!;
        Assert.Equal("brand_new", MemoryState(d));
        Assert.False(((IReadOnlyDictionary<string, string>)d.Inspection!["detectors"]).ContainsKey("files"));
    }
}
