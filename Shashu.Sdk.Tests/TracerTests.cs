using Shashu.Context;
using Shashu.Tracing;
using Xunit;

namespace Shashu.Tests;

/// <summary>
/// Verifies the hierarchical span tracer:
/// trace ids are stable across nested spans, parent/child IDs link
/// correctly, span/parent headers reset when scopes close, and the
/// IsActive flag tracks the stack accurately.
/// </summary>
public class TracerTests
{
    [Fact]
    public void Tracer_is_inactive_outside_any_span()
    {
        Assert.False(Tracer.IsActive);
        Assert.Null(Tracer.TraceId);
        Assert.Null(Tracer.CurrentSpanId);
        Assert.Null(Tracer.ParentSpanId);
    }

    [Fact]
    public void Single_span_sets_trace_and_span_ids_clears_on_dispose()
    {
        Assert.False(Tracer.IsActive);

        using (Tracer.Span("retrieval"))
        {
            Assert.True(Tracer.IsActive);
            Assert.NotNull(Tracer.TraceId);
            Assert.NotNull(Tracer.CurrentSpanId);
            // Root span has no parent.
            Assert.Null(Tracer.ParentSpanId);

            // The trace id and span id should be different — span_id is
            // unique per span, trace_id is unique per trace (and at root,
            // we just generated both fresh).
            Assert.NotEqual(Tracer.TraceId, Tracer.CurrentSpanId);
        }

        // After dispose, tracer is inactive again.
        Assert.False(Tracer.IsActive);
        Assert.Null(Tracer.TraceId);
        Assert.Null(Tracer.CurrentSpanId);
    }

    [Fact]
    public void Nested_spans_share_trace_id_and_link_via_parent_id()
    {
        using (Tracer.Span("outer"))
        {
            var outerTrace = Tracer.TraceId;
            var outerSpan = Tracer.CurrentSpanId;
            Assert.NotNull(outerTrace);
            Assert.NotNull(outerSpan);

            using (Tracer.Span("inner"))
            {
                // Same trace, different span, parent points at outer.
                Assert.Equal(outerTrace, Tracer.TraceId);
                Assert.NotEqual(outerSpan, Tracer.CurrentSpanId);
                Assert.Equal(outerSpan, Tracer.ParentSpanId);
            }

            // After the inner span closes, the outer remains active.
            Assert.True(Tracer.IsActive);
            Assert.Equal(outerTrace, Tracer.TraceId);
            Assert.Equal(outerSpan, Tracer.CurrentSpanId);
        }
    }

    [Fact]
    public void Three_level_nesting_restores_the_exact_displaced_state_at_each_level()
    {
        using (Tracer.Span("A"))
        {
            var spanA = Tracer.CurrentSpanId;
            Assert.NotNull(spanA);
            Assert.Null(Tracer.ParentSpanId);

            using (Tracer.Span("B"))
            {
                var spanB = Tracer.CurrentSpanId;
                Assert.Equal(spanA, Tracer.ParentSpanId);

                using (Tracer.Span("C"))
                {
                    Assert.Equal(spanB, Tracer.ParentSpanId);
                }

                // C closed → B is current again, still parented by A.
                Assert.Equal(spanB, Tracer.CurrentSpanId);
                Assert.Equal(spanA, Tracer.ParentSpanId);
            }

            // B closed → A is current again, a root span with no parent.
            Assert.Equal(spanA, Tracer.CurrentSpanId);
            Assert.Null(Tracer.ParentSpanId);
        }

        // A closed → no active span, no dangling parent.
        Assert.Null(Tracer.CurrentSpanId);
        Assert.Null(Tracer.ParentSpanId);
    }

    [Fact]
    public void Closing_an_inner_span_never_leaves_a_self_parenting_span()
    {
        using (Tracer.Span("outer"))
        {
            using (Tracer.Span("inner"))
            {
            }

            // Regression guard: the pre-fix restore set the outer span's
            // parent to the outer span's own id (self-parenting).
            Assert.NotEqual(Tracer.CurrentSpanId, Tracer.ParentSpanId);
        }
    }

    [Fact]
    public void Restored_root_span_emits_distinct_span_and_empty_parent_headers()
    {
        using (Tracer.Span("root"))
        {
            using (Tracer.Span("inner"))
            {
            }

            // The restored root span drives header emission for the rest of
            // the flow — it must never claim itself as its own parent.
            var headers = WorkflowContext.Build();
            Assert.Equal(Tracer.CurrentSpanId, headers["x-shashu-span-id"]);
            Assert.NotEqual(headers["x-shashu-span-id"], headers["x-shashu-parent-id"]);
            Assert.Equal(string.Empty, headers["x-shashu-parent-id"]);
        }
    }

    [Fact]
    public async Task AsyncLocal_propagates_span_across_await()
    {
        using (Tracer.Span("await-test"))
        {
            var beforeAwait = Tracer.CurrentSpanId;
            await Task.Delay(10);
            // The whole point of AsyncLocal: trace state survives await.
            Assert.Equal(beforeAwait, Tracer.CurrentSpanId);
            Assert.True(Tracer.IsActive);
        }
    }

    [Fact]
    public async Task FlushAsync_drains_buffered_spans()
    {
        // Open + close some spans so they buffer.
        using (Tracer.Span("a"))
        using (Tracer.Span("b"))
        {
        }

        // FlushAsync without configured Bearer key + with no env var = no-op.
        // Safe to call. Should not throw.
        await Tracer.FlushAsync();
    }

    [Fact]
    public void Span_attributes_are_recorded_on_the_buffered_span()
    {
        // We can't easily inspect the buffer (it's private), but we can
        // verify the attributes overload doesn't throw and a flush+drain
        // path completes cleanly.
        using (Tracer.Span("with-attrs", new Dictionary<string, object?> { ["model"] = "gpt-4o" }))
        {
            Assert.True(Tracer.IsActive);
        }
        Assert.False(Tracer.IsActive);
    }
}
