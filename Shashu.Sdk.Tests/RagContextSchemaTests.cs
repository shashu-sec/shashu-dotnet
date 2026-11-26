using System.Text.Json.Nodes;
using Shashu.Tests.Harness;
using Xunit;

namespace Shashu.Tests;

/// <summary>
/// Holds the test harness's schema validator to the real contract, and the
/// SDK's restated <c>source</c> enum to the schema's.
/// </summary>
/// <remarks>
/// The validator is the instrument every other retrieval test measures with, so
/// it is calibrated first: the contract ships a fixture corpus naming exactly
/// what must be accepted and what must be rejected, and a validator that
/// silently ignored a keyword fails here rather than passing a malformed
/// payload somewhere it matters.
/// </remarks>
public class RagContextSchemaTests
{
    public static TheoryData<string> ValidFixtures => Fixtures("valid");
    public static TheoryData<string> InvalidFixtures => Fixtures("invalid");

    private static TheoryData<string> Fixtures(string kind)
    {
        var dir = Path.Combine(RagContextSchema.Directory.FullName, "fixtures", "rag_context", kind);
        var data = new TheoryData<string>();
        if (!Directory.Exists(dir))
        {
            data.Add("(none)");
            return data;
        }
        foreach (var file in Directory.GetFiles(dir, "*.json").OrderBy(f => f, StringComparer.Ordinal))
        {
            if (new FileInfo(file).Length == 0)
                continue;
            data.Add(Path.GetFileName(file));
        }
        return data;
    }

    private static JsonNode? Fixture(string kind, string name)
    {
        var path = Path.Combine(
            RagContextSchema.Directory.FullName, "fixtures", "rag_context", kind, name);
        return JsonNode.Parse(File.ReadAllText(path));
    }

    [Fact]
    public void The_contract_is_found_by_walking_up_from_the_test_binaries()
    {
        Assert.True(RagContextSchema.Directory.Exists);
        Assert.Equal(
            "https://shashu.ai/contracts/rag_context.v1.schema.json",
            RagContextSchema.Document["$id"]!.GetValue<string>());
    }

    [Fact]
    public void The_fixture_corpus_is_not_empty()
    {
        var validDir = Path.Combine(RagContextSchema.Directory.FullName, "fixtures", "rag_context", "valid");
        if (!Directory.Exists(validDir))
            return;
        Assert.True(ValidFixtures.Count >= 8, $"only {ValidFixtures.Count} valid fixtures");
        Assert.True(InvalidFixtures.Count >= 15, $"only {InvalidFixtures.Count} invalid fixtures");
    }

    [Theory]
    [MemberData(nameof(ValidFixtures))]
    public void Every_valid_fixture_is_accepted(string name)
    {
        if (name == "(none)") return;
        var payload = Fixture("valid", name);
        var errors = RagContextSchema.Validate(payload);
        Assert.True(errors.Count == 0, $"{name} should validate but: {string.Join("; ", errors)}");
    }

    [Theory]
    [MemberData(nameof(InvalidFixtures))]
    public void Every_invalid_fixture_is_rejected(string name)
    {
        if (name == "(none)") return;
        var payload = Fixture("invalid", name);
        Assert.False(RagContextSchema.IsValid(payload), $"{name} should have been rejected");
    }

    [Fact]
    public void The_SDK_restated_source_enum_matches_the_contract()
    {
        // The SDK restates the enum so it needs no JSON file on disk to make a
        // call. This is the fence that keeps the copy honest.
        var fromSchema = RagContextSchema.Document["properties"]!["source"]!["enum"]!
            .AsArray().Select(v => v!.GetValue<string>()).ToArray();

        Assert.Equal(fromSchema, SdkSurface.Sources);
    }

    [Fact]
    public void The_SDK_caps_match_the_contract()
    {
        var docs = RagContextSchema.Document["properties"]!["docs"]!;
        Assert.Equal(64, docs["maxItems"]!.GetValue<int>());
        Assert.Equal(2048, docs["items"]!["properties"]!["preview"]!["maxLength"]!.GetValue<int>());
        Assert.Equal(256, RagContextSchema.Document["properties"]!["corpus"]!["maxLength"]!.GetValue<int>());
        Assert.Equal(4096, RagContextSchema.Document["properties"]!["query"]!["maxLength"]!.GetValue<int>());

        Assert.Equal(64, SdkSurface.MaxDocs);
        Assert.Equal(2048, SdkSurface.MaxPreviewChars);
        Assert.Equal(256, SdkSurface.MaxCorpusChars);
        Assert.Equal(4096, SdkSurface.MaxQueryChars);
    }
}
