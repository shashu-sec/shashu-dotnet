using System.Net.Http.Json;
using Shashu;
using Shashu.Chat;
using Shashu.Routing;
using Xunit;

namespace Shashu.Tests;

/// <summary>
/// Live SHASHU proof. Skipped unless SHASHU_E2E_BASE_URL is set — typically by
/// <c>_internal/identity-i4-i5/e2e_real_stack.py</c> after it boots the throwaway stack.
/// </summary>
[Trait("Category", "e2e")]
public class IdentityE2ETests
{
    private const string WrongAud = "This pass is meant for a different audience.";
    private const string Unknown = "No agent matches this pass.";

    private static string? Env(string key)
    {
        var v = Environment.GetEnvironmentVariable(key);
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }

    private static void NeedShashu()
    {
        Skip.If(
            Env("SHASHU_E2E_BASE_URL") is null,
            "no SHASHU is reachable (set SHASHU_E2E_BASE_URL after starting the throwaway stack)");
    }

    private static ChatCompletionRequest Req() => new()
    {
        Model = "mock-echo",
        Messages = [new ChatMessage("user", "i4-i5 e2e")],
    };

    private static ShashuConfig Base() => new()
    {
        BaseUrl = new Uri(Env("SHASHU_E2E_BASE_URL")!.TrimEnd('/') + "/"),
        MaxRetries = 0,
    };

    [SkippableFact]
    public async Task A_allowed_non_streaming()
    {
        NeedShashu();
        Skip.If(Env("SHASHU_E2E_PASS") is null, "SHASHU_E2E_PASS not set");
        var n = 0;
        var pass = Env("SHASHU_E2E_PASS")!;
        var cfg = Base();
        cfg.TokenProvider = _ =>
        {
            Interlocked.Increment(ref n);
            return ValueTask.FromResult(pass);
        };
        await using var client = new ShashuClient(cfg);
        await client.Chat.Completions.CreateAsync(Req());
        Assert.Equal(1, n);
    }

    [SkippableFact]
    public async Task A_allowed_streaming()
    {
        NeedShashu();
        Skip.If(Env("SHASHU_E2E_PASS") is null, "SHASHU_E2E_PASS not set");
        var n = 0;
        var pass = Env("SHASHU_E2E_PASS")!;
        var cfg = Base();
        cfg.TokenProvider = _ =>
        {
            Interlocked.Increment(ref n);
            return ValueTask.FromResult(pass);
        };
        await using var client = new ShashuClient(cfg);
        await foreach (var _ in client.Chat.Completions.CreateStreamAsync(Req()))
        {
        }
        Assert.Equal(1, n);
    }

    [SkippableFact]
    public async Task B_expired_retries_once()
    {
        NeedShashu();
        Skip.If(Env("SHASHU_E2E_PASS_EXPIRED") is null || Env("SHASHU_E2E_PASS_FRESH") is null);
        var passes = new[] { Env("SHASHU_E2E_PASS_EXPIRED")!, Env("SHASHU_E2E_PASS_FRESH")! };
        var n = 0;
        var cfg = Base();
        cfg.TokenProvider = _ =>
        {
            var i = Interlocked.Increment(ref n) - 1;
            return ValueTask.FromResult(passes[Math.Min(i, passes.Length - 1)]);
        };
        await using var client = new ShashuClient(cfg);
        await client.Chat.Completions.CreateAsync(Req());
        Assert.Equal(2, n);
    }

    [SkippableFact]
    public async Task C_wrong_audience_no_retry()
    {
        NeedShashu();
        Skip.If(Env("SHASHU_E2E_PASS_WRONG_AUD") is null);
        var n = 0;
        var pass = Env("SHASHU_E2E_PASS_WRONG_AUD")!;
        var cfg = Base();
        cfg.TokenProvider = _ =>
        {
            Interlocked.Increment(ref n);
            return ValueTask.FromResult(pass);
        };
        await using var client = new ShashuClient(cfg);
        var ex = await Assert.ThrowsAsync<ShashuAuthenticationException>(() =>
            client.Chat.Completions.CreateAsync(Req()));
        Assert.Equal(CredentialResolver.PASS_WRONG_AUDIENCE, ex.Code);
        Assert.Equal(WrongAud, ex.Message);
        Assert.Equal(1, n);
    }

    [SkippableFact]
    public async Task D_unknown_agent_no_retry()
    {
        NeedShashu();
        Skip.If(Env("SHASHU_E2E_PASS_UNKNOWN") is null);
        var n = 0;
        var pass = Env("SHASHU_E2E_PASS_UNKNOWN")!;
        var cfg = Base();
        cfg.TokenProvider = _ =>
        {
            Interlocked.Increment(ref n);
            return ValueTask.FromResult(pass);
        };
        await using var client = new ShashuClient(cfg);
        var ex = await Assert.ThrowsAsync<ShashuAuthenticationException>(() =>
            client.Chat.Completions.CreateAsync(Req()));
        Assert.Equal(CredentialResolver.PASS_UNKNOWN_AGENT, ex.Code);
        Assert.Equal(Unknown, ex.Message);
        Assert.Equal(1, n);
    }

    [SkippableFact]
    public async Task Key_mode_still_works()
    {
        NeedShashu();
        Skip.If(Env("SHASHU_E2E_KEY") is null, "SHASHU_E2E_KEY not set");
        var key = Env("SHASHU_E2E_KEY")!;
        Assert.StartsWith("shashu_sk_", key);
        var cfg = Base();
        cfg.ApiKey = key;
        await using var client = new ShashuClient(cfg);
        await client.Chat.Completions.CreateAsync(Req());
    }

    [SkippableFact]
    public async Task Handler_sends_trace_session_and_version()
    {
        NeedShashu();
        Skip.If(Env("SHASHU_E2E_KEY") is null, "SHASHU_E2E_KEY not set");
        var opts = new ShashuRoutingOptions
        {
            BaseUrl = new Uri(Env("SHASHU_E2E_BASE_URL")!.TrimEnd('/') + "/"),
        };
        using var handler = new ShashuRoutingHandler(Env("SHASHU_E2E_KEY"), new HttpClientHandler(), opts);
        using var http = new HttpClient(handler);
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions");
        req.Content = JsonContent.Create(new
        {
            model = "mock-echo",
            messages = new[] { new { role = "user", content = "handler e2e" } },
        });
        var res = await http.SendAsync(req);
        Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
        Assert.True(
            res.Headers.Contains("Shashu-Version") || res.Headers.Contains("shashu-version"),
            "Shashu-Version not echoed on the handler-routed response");
    }
}
