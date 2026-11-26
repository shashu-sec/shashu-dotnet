using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Shashu;
using Shashu.Chat;
using Shashu.Tests.Harness;
using Xunit;

namespace Shashu.Tests;

public class Rebrand120Tests
{
    private const string Copyright = "Trustgate AI LLC";
    private const string Key = "shashu_sk_rebrand_test_key_not_live";

    [Fact]
    public async Task A_PassthroughPrefix_RaisesAuthenticationException()
    {
        var stub = new StubHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent(
                    "{\"error\":{\"message\":\"SHASHU Provider Error: Invalid API key\",\"type\":\"authentication_error\",\"code\":\"401\"},\"choices\":[]}"),
            }));
        using var http = new HttpClient(stub) { BaseAddress = new Uri("https://test.invalid/") };
        using var client = new ShashuClient(
            new ShashuConfig { BaseUrl = TestGateway.Uri, ApiKey = Key, MaxRetries = 0 }, http);

        var ex = await Assert.ThrowsAsync<ShashuAuthenticationException>(() =>
            client.Chat.Completions.CreateAsync(new ChatCompletionRequest
            {
                Model = "gpt-4o",
                Messages = new[] { new ChatMessage("user", "hi") },
            }));
        Assert.StartsWith("SHASHU Provider Error:", ex.Message);
    }

    [Fact]
    public async Task C_AuthorizationSendsShashuSk()
    {
        string? auth = null;
        var stub = new StubHandler((req, _) =>
        {
            auth = req.Headers.Authorization!.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new
                {
                    id = "c",
                    choices = new[]
                    {
                        new { index = 0, message = new { role = "assistant", content = "ok" } },
                    },
                }),
            });
        });
        using var http = new HttpClient(stub) { BaseAddress = new Uri("https://test.invalid/") };
        using var client = new ShashuClient(
            new ShashuConfig { BaseUrl = TestGateway.Uri, ApiKey = Key }, http);
        await client.Chat.Completions.CreateAsync(new ChatCompletionRequest
        {
            Model = "gpt-4o",
            Messages = new[] { new ChatMessage("user", "hi") },
        });
        Assert.Equal("Bearer " + Key, auth);
        Assert.StartsWith("shashu_sk_", Key);
    }

    [Fact]
    public void D_NoTrustgateInShippedTree()
    {
        var root = FindRepoRoot();
        var hits = new List<string>();
        var files = new List<string>
        {
            Path.Combine(root, "README.md"),
            Path.Combine(root, "CHANGELOG.md"),
            Path.Combine(root, "NOTICE"),
            Path.Combine(root, "LICENSE"),
            Path.Combine(root, "Shashu.Sdk", "Shashu.Sdk.csproj"),
        };
        files.AddRange(Directory.GetFiles(Path.Combine(root, "Shashu.Sdk"), "*", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")));
        var contracts = Path.Combine(root, "contracts");
        if (Directory.Exists(contracts))
            files.AddRange(Directory.GetFiles(contracts, "*", SearchOption.AllDirectories));

        var needle = new Regex("trustgate", RegexOptions.IgnoreCase);
        foreach (var file in files.Distinct())
        {
            if (!File.Exists(file)) continue;
            if (file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;
            string text;
            try { text = File.ReadAllText(file); }
            catch { continue; }
            var stripped = text.Replace(Copyright, "", StringComparison.OrdinalIgnoreCase);
            foreach (Match m in needle.Matches(stripped))
                hits.Add($"{Path.GetRelativePath(root, file)}:{m.Index}");
        }
        Assert.Empty(hits);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Shashu.Sdk", "Shashu.Sdk.csproj")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("SDK repo root");
    }
}
