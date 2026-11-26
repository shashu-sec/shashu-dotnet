using System.Reflection;
using Shashu.Internal;
using Shashu.Tests.Harness;
using Xunit;

namespace Shashu.Tests;

/// <summary>
/// Holds the public retrieval surface to <c>contracts/sdk-surface.v1.md</c>.
/// </summary>
/// <remarks>
/// The contract requires each SDK to carry a test asserting its exported names
/// are exactly the expected set, "so a new export fails the build. That test
/// exists because a name did leak: Node exported a third retrieval function,
/// <c>isolateRetrieval</c>, whose customer-facing job <c>retrievalScope</c>
/// already covered." This is that test for .NET.
/// </remarks>
public class SdkSurfaceTests
{
    /// <summary>The holder the contract names for .NET: <c>Shashu.Retrieval</c>.</summary>
    /// <remarks>
    /// Resolved by name rather than with <c>typeof</c> on purpose. The holder's
    /// name IS part of the contract, so renaming it has to fail as a red test
    /// with a readable message — not merely as a compile error in whichever
    /// file happens to mention the type first.
    /// </remarks>
    private static readonly Type? Holder =
        typeof(ShashuClient).Assembly.GetType("Shashu.Retrieval");

    private static Type Surface =>
        Holder ?? throw new InvalidOperationException(
            "The public type Shashu.Retrieval is not in the assembly. "
            + "contracts/sdk-surface.v1.md names it as .NET's retrieval holder.");

    [Fact]
    public void The_retrieval_holder_is_named_for_what_it_does_not_for_the_product()
    {
        // A holder named Shashu inside namespace Shashu shadowed the
        // namespace: customers wrote `using static Shashu.Shashu;` and
        // the assembly needed globally-qualified references internally.
        // Renamed before first release; the contract's .NET row is the
        // authority and this is what holds the code to it.
        var exported = typeof(ShashuClient).Assembly
            .GetExportedTypes().Select(t => t.FullName).ToArray();

        Assert.Contains("Shashu.Retrieval", exported);
        Assert.Contains("Shashu.Memory", exported);
        // Shashu.Shashu is the decision/block surface (DecisionOf,
        // IsBlock, DecisionFrom), not the retrieval holder.
        Assert.Contains("Shashu.Shashu", exported);
        Assert.Null(typeof(Shashu).GetMethod("DeclareRetrieval"));
        Assert.Null(typeof(Shashu).GetMethod("RetrievalScope"));
    }

    [Fact]
    public void The_contracts_dot_NET_row_names_the_holder_this_assembly_actually_exports()
    {
        // The spec row and the shipped form are asserted against each other so
        // neither can drift alone.
        var row = File.ReadAllLines(Path.Combine(
                RagContextSchema.Directory.FullName, "sdk-surface.v1.md"))
            .FirstOrDefault(l => l.TrimStart().StartsWith("| .NET | static holder named", StringComparison.Ordinal));

        Assert.NotNull(row);
        Assert.Contains($"static holder named `{Surface.Name}`", row);
        Assert.Contains("using static Shashu.Retrieval;", row);
        Assert.Contains("DeclareRetrieval(docs, corpus: ...)", row);
    }

    [Fact]
    public void There_are_exactly_two_public_retrieval_members_and_no_third()
    {
        var names = Surface
            .GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Select(m => m.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["DeclareRetrieval", "RetrievalScope"], names);
    }

    [Fact]
    public void The_surface_type_exposes_no_public_fields_or_properties()
    {
        Assert.Empty(Surface.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly));
        Assert.Empty(Surface.GetProperties(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly));
    }

    [Fact]
    public void The_bus_the_joiner_and_the_builder_stay_internal()
    {
        // The contract: "Anything not on this page is internal and must not be
        // exported."
        foreach (var type in new[]
                 {
                     typeof(RetrievalBus), typeof(RetrievalJoiner),
                     typeof(RagContextBuilder), typeof(RetrievalEvent), typeof(RagContextDoc),
                     typeof(MemoryContextBuilder), typeof(MemoryEvent),
                 })
        {
            Assert.False(type.IsPublic, $"{type.Name} must not be public");
        }
    }

    [Theory]
    [InlineData("DeclareRetrieval")]
    [InlineData("RetrievalScope")]
    public void Argument_order_names_and_defaults_are_the_frozen_ones(string method)
    {
        var parameters = Surface.GetMethod(method)!.GetParameters();

        Assert.Equal(["docs", "corpus", "query", "source"], parameters.Select(p => p.Name!).ToArray());

        Assert.False(parameters[0].HasDefaultValue);          // docs — required
        Assert.False(parameters[1].HasDefaultValue);          // corpus — required
        Assert.True(parameters[2].HasDefaultValue);
        Assert.Null(parameters[2].DefaultValue);              // query — default none
        Assert.True(parameters[3].HasDefaultValue);
        Assert.Equal("manual", parameters[3].DefaultValue);   // source — default "manual"
    }

    [Fact]
    public void RetrievalScope_returns_something_a_using_statement_accepts()
    {
        Assert.True(typeof(IDisposable).IsAssignableFrom(
            Surface.GetMethod("RetrievalScope")!.ReturnType));
    }

    [Fact]
    public void SdkVersion_matches_the_csproj_Version()
    {
        var csproj = File.ReadAllText(Path.Combine(
            RagContextSchema.Directory.FullName, "..", "Shashu.Sdk", "Shashu.Sdk.csproj"));
        var match = System.Text.RegularExpressions.Regex.Match(
            csproj, @"<Version>([^<]+)</Version>");
        Assert.True(match.Success, "Shashu.Sdk.csproj has no <Version>");
        Assert.Equal(ShashuClient.SdkVersion, match.Groups[1].Value);
    }

    [Fact]
    public void DefaultBaseUrl_is_gone_from_the_public_surface()
    {
        // It was `public static readonly Uri DefaultBaseUrl`. Deleted rather
        // than deprecated: keeping the export keeps the exact string the change
        // exists to remove.
        Assert.Null(typeof(ShashuClient).GetField(
            "DefaultBaseUrl", BindingFlags.Public | BindingFlags.Static));
        Assert.Null(typeof(ShashuClient).GetProperty(
            "DefaultBaseUrl", BindingFlags.Public | BindingFlags.Static));
    }

    [Fact]
    public void No_public_member_anywhere_in_the_assembly_hands_back_a_default_address()
    {
        // A constant or default-valued property carrying a gateway hostname is
        // the same defect wherever it lives, so the whole public surface is
        // swept rather than the one field that used to hold it.
        var offenders = new List<string>();

        foreach (var type in typeof(ShashuClient).Assembly.GetExportedTypes())
        {
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                if (!field.IsLiteral && !field.IsInitOnly) continue;
                var value = field.GetValue(null)?.ToString();
                if (LooksLikeAGatewayAddress(value)) offenders.Add($"{type.Name}.{field.Name} = {value}");
            }
        }

        Assert.True(offenders.Count == 0, string.Join("; ", offenders));
    }

    [Fact]
    public void The_decision_exposes_exactly_the_reading_members_sdk_surface_names()
    {
        // FILE-06 added the file / memory / route members. A new public member
        // on the decision is a change to the contract, so it fails here first.
        // `Inspection` (2026-10-20) is parity with Python's and Node's existing
        // `inspection`; sdk-surface.v1.md is vendored byte-for-byte into those two
        // repos, so its table is not edited from this one.
        var names = typeof(ShashuDecision)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[]
        {
            "Attachments", "Code", "Decision", "FilesBlocked", "FilesUnverified", "Id", "Inspection", "Memory",
            "MemoryBlocked", "MemoryCoverage", "MemoryRecallsFlagged", "Retryable", "RoutePolicy",
        }, names);
        var contract = File.ReadAllText(Path.Combine(RagContextSchema.Directory.FullName, "sdk-surface.v1.md"));
        foreach (var member in new[]
                 {
                     "Attachments", "FilesUnverified", "FilesBlocked", "Memory", "MemoryBlocked",
                     "MemoryRecallsFlagged", "MemoryCoverage", "RoutePolicy", "MemoryWriteBlocked",
                 })
        {
            Assert.Contains(member, contract);
        }
    }

    [Theory]
    [InlineData("Shashu.Attachments")]
    [InlineData("Shashu.Attachment")]
    [InlineData("Shashu.AttachmentFinding")]
    [InlineData("Shashu.AttachmentState")]
    [InlineData("Shashu.MemoryActivity")]
    [InlineData("Shashu.MemoryItem")]
    [InlineData("Shashu.RoutePolicy")]
    [InlineData("Shashu.MemoryWriteBlocked")]
    public void The_block_types_are_exported(string fullName) =>
        Assert.Contains(fullName, typeof(ShashuClient).Assembly.GetExportedTypes().Select(t => t.FullName));

    [Fact]
    public void The_block_records_have_no_public_setters()
    {
        foreach (var type in new[]
                 {
                     typeof(Attachments), typeof(Attachment), typeof(AttachmentFinding),
                     typeof(MemoryActivity), typeof(MemoryItem), typeof(RoutePolicy),
                 })
        {
            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var setter = prop.SetMethod;
                var initOnly = setter?.ReturnParameter.GetRequiredCustomModifiers()
                    .Contains(typeof(System.Runtime.CompilerServices.IsExternalInit)) ?? true;
                Assert.True(initOnly, $"{type.Name}.{prop.Name} has a public setter");
            }
        }
        Assert.True(typeof(ShashuPolicyViolation).IsAssignableFrom(typeof(MemoryWriteBlocked)));
        Assert.Equal(AttachmentState.Unknown, default(AttachmentState));
    }

    private static bool LooksLikeAGatewayAddress(string? value) =>
        value is not null
        && (value.Contains("trustgateai.io", StringComparison.OrdinalIgnoreCase)
            || value.Contains("trustgate.ai", StringComparison.OrdinalIgnoreCase));
}
