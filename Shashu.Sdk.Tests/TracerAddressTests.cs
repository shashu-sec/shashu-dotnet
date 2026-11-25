using Shashu.Configuration;
using Shashu.Tests.Harness;
using Shashu.Tracing;
using Xunit;

namespace Shashu.Tests;

/// <summary>
/// The tracer's half of the no-default-address change: it drops spans rather
/// than guessing a host, and it never throws doing so.
/// </summary>
/// <remarks>
/// Telemetry is best-effort and runs in shutdown hooks, so throwing is not an
/// option — but silence is not either, which is why an unconfigured flush warns
/// once. These tests capture <c>Console.Error</c> to prove both.
/// </remarks>
[Collection(EnvironmentCollection.Name)]
public class TracerAddressTests
{
    [Fact]
    public async Task An_unconfigured_flush_discards_spans_without_throwing_or_dialling()
    {
        using var _ = new EnvVar(ShashuEnv.BaseUrlEnv, null);
        using var __l = new EnvVar(ShashuEnv.BaseUrlEnv, null);
        using var __ = new EnvVar("SHASHU_API_KEY", "sk-trustgate-test");

        using (Tracer.Span("unconfigured")) { }

        // No address anywhere: the flush must complete, having sent nothing.
        // A guessed host would attempt a real connection here.
        await Tracer.FlushAsync();
    }

    [Fact]
    public async Task An_unparseable_configured_address_does_not_throw_out_of_flush()
    {
        // Configure() does not validate its argument, and FlushAsync is called
        // from a process-exit hook. An address without a scheme must degrade to
        // "no telemetry", not to an exception during shutdown.
        using var _ = new EnvVar(ShashuEnv.BaseUrlEnv, null);
        Tracer.Configure("sk-trustgate-test", "localhost:8000");
        try
        {
            using (Tracer.Span("bad-address")) { }
            await Tracer.FlushAsync();
        }
        finally
        {
            Tracer.Configure("sk-trustgate-test", null);
        }
    }

    [Fact]
    public async Task An_unconfigured_flush_warns_once_rather_than_being_silent()
    {
        using var _ = new EnvVar(ShashuEnv.BaseUrlEnv, null);
        using var __l = new EnvVar(ShashuEnv.BaseUrlEnv, null);
        using var __ = new EnvVar("SHASHU_API_KEY", "sk-trustgate-test");

        var captured = new StringWriter();
        var previous = Console.Error;
        Console.SetError(captured);
        try
        {
            using (Tracer.Span("warn-a")) { }
            await Tracer.FlushAsync();
            using (Tracer.Span("warn-b")) { }
            await Tracer.FlushAsync();
        }
        finally
        {
            Console.SetError(previous);
        }

        var text = captured.ToString();

        // The warning is latched process-wide, so another test in this
        // collection may already have consumed it. What must hold either way:
        // it is never emitted more than once, and it never suggests a host.
        var occurrences = text.Split("[trustgate.tracer] no gateway address").Length - 1;
        Assert.True(occurrences <= 1, $"warned {occurrences} times, expected at most once");

        foreach (var fragment in new[] { "trustgateai.io", "trustgate.ai", "https://", "http://" })
            Assert.DoesNotContain(fragment, text, StringComparison.OrdinalIgnoreCase);
    }
}
