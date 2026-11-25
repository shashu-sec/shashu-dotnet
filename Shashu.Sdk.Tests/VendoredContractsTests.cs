using System.Security.Cryptography;
using Shashu.Tests.Harness;
using Xunit;

namespace Shashu.Tests;

/// <summary>
/// The vendored contract pages are unmodified copies. Digests are over
/// LF-normalised bytes so a CRLF checkout cannot change them.
/// </summary>
public class VendoredContractsTests
{
    public static TheoryData<string, string> Pinned => new()
    {
        { "sdk-surface.v1.md", "3984c1c3527f515e01a89b1635d3c4f4d42439c74c742c4871de9e555fb9051b" },
        { "decision-envelope.v1.md", "b47bfc39bb02515e68d1b693661b6e7605b921bc7cbe2fae940bcac2f5ff65f6" },
        { "rag_context.v1.schema.json", "25894f3aedbfe96e7e1cdd12ab56d1a316eed8b8fb4dcad603ed4c0d67574fee" },
        { "memory_context.v1.schema.json", "155a01d2263421eeb4376ba54b98ce9e12d660df86ccfc2fb5f54619189ee40e" },
    };

    [Theory]
    [MemberData(nameof(Pinned))]
    public void Vendored_contract_is_unmodified(string name, string expected)
    {
        var path = Path.Combine(RagContextSchema.Directory.FullName, name);
        Assert.True(File.Exists(path), $"{name} is missing from contracts/");
        var raw = File.ReadAllBytes(path);
        var lf = new List<byte>(raw.Length);
        for (var i = 0; i < raw.Length; i++)
        {
            if (raw[i] == (byte)'\r' && i + 1 < raw.Length && raw[i + 1] == (byte)'\n')
                continue;
            lf.Add(raw[i]);
        }
        var digest = Convert.ToHexString(SHA256.HashData(lf.ToArray())).ToLowerInvariant();
        Assert.Equal(expected, digest);
    }
}
