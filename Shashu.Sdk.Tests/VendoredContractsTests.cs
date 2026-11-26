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
        { "sdk-surface.v1.md", "0ee154022de6014efbe6e008460ed8d5f1d07e860ff2bfed726f3a02f5ebbfef" },
        { "decision-envelope.v1.md", "ada3a1771bf593f19c2edf3bf2ec578c6fb69c75399f4007317aedd8f0ca691f" },
        { "rag_context.v1.schema.json", "4cf08786149c40c3a046787f5bcbf1cde9e780bb25008a0abbc894237c8a4b3f" },
        { "memory_context.v1.schema.json", "afe38fb5e45525e94f2285c15ed0de60e614f4661577e7e43af3131743ca7bdf" },
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
