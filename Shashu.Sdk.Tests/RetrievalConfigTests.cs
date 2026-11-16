using System.Text;
using System.Text.Json.Nodes;
using Shashu.Configuration;
using Shashu.Routing;
using Shashu.Internal;
using Shashu.Tests.Harness;
using Xunit;

namespace Shashu.Tests;

/// <summary>
/// Cases 9 and 10: the kill switch, and the address that has no default.
/// </summary>
/// <remarks>
/// Environment variables are process-global, so xUnit must not run these
/// alongside anything else that reads one. The collection below disables
/// parallelisation for exactly that reason; without it these pass or fail by
/// timing, which is worse than failing.
/// </remarks>
[Collection(EnvironmentCollection.Name)]
public class RetrievalConfigTests
{
    // ----------------------------------------------------- 9. TRUSTGATE_DISABLE

    [Fact]
    public async Task Case09_TRUSTGATE_DISABLE_makes_the_bus_joiner_and_declaration_inert()
    {
        // RED-FIRST: reddened by mutating Retrieval.DeclareRetrieval to drop its
        // `if (ShashuEnv.IsDisabled()) return;` guard ("ignore-disable").
        // The declaration is then pushed and the request carries it.
        using var _ = new EnvVar(ShashuEnv.DisableEnv, "1");
        RetrievalBus.Clear();

        Retrieval.DeclareRetrieval(new[] { "canary-09-disabled" }, corpus: "corpus");
        Assert.False(RetrievalBus.HasEvents());

        using (var scope = Retrieval.RetrievalScope(new[] { "canary-09-scope" }, corpus: "corpus"))
        {
            Assert.False(RetrievalBus.HasEvents());
        }

        var gateway = new MockGateway();
        using (var client = new HttpClient(new ShashuRoutingHandler(
            "sk-trustgate-test", gateway, new ShashuRoutingOptions { BaseUrl = TestGateway.Uri })))
        {
            using var response = await client.SendAsync(
                new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions")
                {
                    Content = new StringContent(
                        new JsonObject { ["model"] = "gpt-4o-mini" }.ToJsonString(),
                        Encoding.UTF8, "application/json"),
                });
        }

        Assert.False(gateway.Single().HasShashuMetadata);
        Assert.DoesNotContain("canary-09", Encoding.UTF8.GetString(gateway.Single().Body));
    }

    [Fact]
    public async Task A_bus_populated_before_the_switch_flipped_is_dropped_not_sent()
    {
        RetrievalBus.Clear();
        Retrieval.DeclareRetrieval(new[] { "canary-09-preexisting" }, corpus: "corpus");
        Assert.True(RetrievalBus.HasEvents());

        using var _ = new EnvVar(ShashuEnv.DisableEnv, "1");

        var gateway = new MockGateway();
        using (var client = new HttpClient(new ShashuRoutingHandler(
            "sk-trustgate-test", gateway, new ShashuRoutingOptions { BaseUrl = TestGateway.Uri })))
        {
            using var response = await client.SendAsync(
                new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions")
                {
                    Content = new StringContent(
                        new JsonObject { ["model"] = "gpt-4o-mini" }.ToJsonString(),
                        Encoding.UTF8, "application/json"),
                });
        }

        Assert.False(gateway.Single().HasShashuMetadata);
        Assert.False(RetrievalBus.HasEvents());
    }

    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("TRUE")]
    [InlineData("yes")]
    [InlineData("on")]
    public void The_kill_switch_accepts_what_platform_tooling_actually_emits(string value)
    {
        using var _ = new EnvVar(ShashuEnv.DisableEnv, value);
        Assert.True(ShashuEnv.IsDisabled());
    }

    [Theory]
    [InlineData("0")]
    [InlineData("")]
    [InlineData("false")]
    [InlineData("no")]
    public void The_kill_switch_stays_off_for_everything_else(string value)
    {
        using var _ = new EnvVar(ShashuEnv.DisableEnv, value);
        Assert.False(ShashuEnv.IsDisabled());
    }

    // ------------------------------------------- 10. the address has no default

    [Fact]
    public void Case10_An_unset_address_throws_rather_than_guessing_a_host()
    {
        using var _ = new EnvVar(ShashuEnv.BaseUrlEnv, null);
        using var __ = new EnvVar(ShashuEnv.BaseUrlEnvLegacy, null);

        var ex = Assert.Throws<ShashuConfigException>(() =>
            new ShashuClient(new ShashuConfig { ApiKey = "sk-trustgate-test" }));

        AssertFrozenMessage(ex.Message, "new ShashuClient()", "ShashuConfig.BaseUrl");
    }

    [Fact]
    public void Case10_The_routing_handler_throws_on_an_unset_address_too()
    {
        using var _ = new EnvVar(ShashuEnv.BaseUrlEnv, null);
        using var __ = new EnvVar(ShashuEnv.BaseUrlEnvLegacy, null);

        var ex = Assert.Throws<ShashuConfigException>(() =>
            new ShashuRoutingHandler("sk-trustgate-test"));

        AssertFrozenMessage(
            ex.Message, "new ShashuRoutingHandler()", "ShashuRoutingOptions.BaseUrl");
    }

    [Fact]
    public void Case10_The_message_names_the_env_var_and_contains_no_hostname()
    {
        using var _ = new EnvVar(ShashuEnv.BaseUrlEnv, null);
        using var __ = new EnvVar(ShashuEnv.BaseUrlEnvLegacy, null);

        var message = Assert.Throws<ShashuConfigException>(() =>
            new ShashuClient(new ShashuConfig { ApiKey = "sk-trustgate-test" })).Message;

        Assert.Contains("SHASHU_BASE_URL", message, StringComparison.Ordinal);
        Assert.Contains("TRUSTGATE_BASE_URL", message, StringComparison.Ordinal);

        foreach (var fragment in new[]
                 {
                     "trustgateai.io", "trustgate.ai", "gateway.trustgate",
                     "http://", "https://", "://",
                 })
        {
            Assert.DoesNotContain(fragment, message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void The_env_var_supplies_the_address_when_no_option_does()
    {
        using var _ = new EnvVar(ShashuEnv.BaseUrlEnv, "https://gw.internal.example.invalid/");

        using var client = new ShashuClient(
            new ShashuConfig { ApiKey = "sk-trustgate-test" });

        Assert.NotNull(client.Chat);
    }

    [Fact]
    public void An_explicit_option_wins_over_the_environment()
    {
        using var _ = new EnvVar(ShashuEnv.BaseUrlEnv, "https://from-env.example.invalid/");

        var resolved = ShashuEnv.RequireBaseUrl(
            new Uri("https://from-option.example.invalid/"), "entry()", "arg");

        Assert.Equal("https://from-option.example.invalid/", resolved.ToString());
    }

    [Fact]
    public void ShashuConfigException_is_catchable_as_ShashuException()
    {
        using var _ = new EnvVar(ShashuEnv.BaseUrlEnv, null);
        using var __ = new EnvVar(ShashuEnv.BaseUrlEnvLegacy, null);

        // A caller with one catch around their gateway calls still catches it.
        var ex = Assert.Throws<ShashuConfigException>(() =>
            new ShashuClient(new ShashuConfig { ApiKey = "sk-trustgate-test" }));

        Assert.IsAssignableFrom<ShashuException>(ex);
        Assert.Equal(0, ex.StatusCode);          // no request was ever made
        Assert.Equal(string.Empty, ex.ResponseBody);
    }

    /// <summary>
    /// The frozen template from <c>contracts/sdk-surface.v1.md</c>, with .NET's
    /// two substitutions. Asserted whole rather than by fragments: the contract
    /// freezes the wording, and a reworded message is a v2 change.
    /// </summary>
    private static void AssertFrozenMessage(string message, string entryPoint, string argument)
    {
        Assert.Equal(
            $"SHASHU gateway address is not configured, so {entryPoint} has "
            + $"nothing to route to. Pass {argument} to {entryPoint}, or set one of the "
            + "SHASHU_BASE_URL or TRUSTGATE_BASE_URL environment variables. SHASHU is self-hosted: the "
            + "SDK has no default address and will not guess one.",
            message);
    }
}

/// <summary>Sets an environment variable for the life of the test, then puts it back.</summary>
internal sealed class EnvVar : IDisposable
{
    private readonly string _name;
    private readonly string? _previous;

    internal EnvVar(string name, string? value)
    {
        _name = name;
        _previous = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);
    }

    public void Dispose() => Environment.SetEnvironmentVariable(_name, _previous);
}

/// <summary>
/// Serialises every test that reads or writes an environment variable.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public class EnvironmentCollection
{
    internal const string Name = "environment";
}
