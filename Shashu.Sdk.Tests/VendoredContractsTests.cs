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
        { "sdk-surface.v1.md", "166630b34367bfec8499c996badaece5250102d4aa71f68be6519b243e75008f" },
        { "decision-envelope.v1.md", "fa98318efb952ff586116adaf5cd2b6f067f5847bbfd475f16b46dd38e56ca78" },
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
