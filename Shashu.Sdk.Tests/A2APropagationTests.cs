using System.Net;
using System.Net.Http.Json;
using Shashu;
using Shashu.Chat;
using Shashu.Context;
using Shashu.Tracing;
using Xunit;
using Shashu.Tests.Harness;

namespace Shashu.Tests;

/// <summary>
/// WP-A2A-4 / P4-c — A2A inbound-capture + outbound propagation (ADR-007 §0, §4.1).
/// Mirrors the corrected Python reference (trustgate 0.9.1) and the Node SDK (1.7.1):
/// the gateway call FORWARDS the captured upstream caller (never self), and the invoking
/// side stamps caller = self. Capture is exercised inside <c>Tracer.Incoming(...)</c>
/// scopes so AsyncLocal state is deterministic per-case (no leakage between tests).
/// </summary>
public class A2APropagationTests
{
    private static Dictionary<string, string> H(params (string Key, string Value)[] kv)
    {
        var d = new Dictionary<string, string>();
        foreach (var (k, v) in kv) d[k] = v;
        return d;
    }

    // --------------------------------------------------------------------- //
    // Inbound capture
    // --------------------------------------------------------------------- //
    [Fact]
    public void Capture_populates_store_including_inbound_caller()
    {
        using (Tracer.Incoming(H(
            ("x-shashu-trace-id", "flow-1"), ("x-shashu-session-id", "sess-1"),
            ("x-shashu-hop-depth", "2"), ("x-shashu-caller-agent-id", "agent-A"))))
        {
            Assert.True(Tracer.IsA2aHop);
            Assert.Equal("flow-1", Tracer.TraceId);
            Assert.Equal("sess-1", Tracer.SessionId);
            Assert.Equal(2, Tracer.InboundHopDepth);
            Assert.Equal(3, Tracer.OutboundHopDepth);
            Assert.Equal("agent-A", Tracer.InboundCallerAgentId); // §4.1: the upstream invoker
        }
        Assert.False(Tracer.IsA2aHop);
    }

    [Fact]
    public void No_inbound_trace_is_origin_returns_false()
    {
        using (Tracer.Incoming(H()))
        {
            Assert.False(Tracer.CaptureIncoming(H(("x-shashu-session-id", "s"))));
            Assert.False(Tracer.CaptureIncoming(H()));
            Assert.False(Tracer.IsA2aHop);
        }
    }

    [Fact]
    public void Capture_is_case_insensitive()
    {
        using (Tracer.Incoming(H(
            ("X-Shashu-Trace-Id", "T1"), ("X-Shashu-Session-Id", "S1"),
            ("X-Shashu-Caller-Agent-Id", "A1"))))
        {
            Assert.Equal("T1", Tracer.TraceId);
            Assert.Equal("S1", Tracer.SessionId);
            Assert.Equal("A1", Tracer.InboundCallerAgentId);
        }
    }

    [Fact]
    public void Capture_accepts_HttpHeaders()
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://x.invalid/");
        req.Headers.TryAddWithoutValidation("x-shashu-trace-id", "T2");
        req.Headers.TryAddWithoutValidation("x-shashu-hop-depth", "5");
        using (Tracer.Incoming(req.Headers))
        {
            Assert.Equal("T2", Tracer.TraceId);
            Assert.Equal(6, Tracer.OutboundHopDepth);
        }
    }

    [Fact]
    public void No_hop_header_defaults_outbound_to_one()
    {
        using (Tracer.Incoming(H(("x-shashu-trace-id", "f"))))
        {
            Assert.Equal(0, Tracer.InboundHopDepth);
            Assert.Equal(1, Tracer.OutboundHopDepth);
        }
    }

    [Fact]
    public void No_caller_leaves_inbound_caller_null()
    {
        using (Tracer.Incoming(H(("x-shashu-trace-id", "f"), ("x-shashu-hop-depth", "1"))))
        {
            Assert.Null(Tracer.InboundCallerAgentId);
        }
    }

    // --------------------------------------------------------------------- //
    // Outbound gateway call (WorkflowContext.Build): propagate, FORWARD caller
    // --------------------------------------------------------------------- //
    [Fact]
    public void Gateway_propagates_captured_trace_with_session_and_hop()
    {
        using (Tracer.Incoming(H(
            ("x-shashu-trace-id", "flow-xyz"), ("x-shashu-session-id", "sess-1"),
            ("x-shashu-hop-depth", "2"), ("x-shashu-caller-agent-id", "agent-A"))))
        {
            var h = WorkflowContext.Build();
            Assert.Equal("flow-xyz", h["x-shashu-trace-id"]); // propagated, not a fresh GUID
            Assert.Equal("sess-1", h["x-shashu-session-id"]);
            Assert.Equal("3", h["x-shashu-hop-depth"]); // inbound 2 + 1
        }
    }

    [Fact]
    public void Gateway_forwards_inbound_caller_not_self()
    {
        using (Tracer.Incoming(H(
            ("x-shashu-trace-id", "F"), ("x-shashu-caller-agent-id", "agent-A"),
            ("x-shashu-hop-depth", "1"))))
        {
            Assert.Equal("agent-A", WorkflowContext.Build()["x-shashu-caller-agent-id"]);
        }
    }

    [Fact]
    public void Origin_emits_no_lineage_headers()
    {
        using (Tracer.Incoming(H())) // explicit origin scope
        {
            var h = WorkflowContext.Build();
            Assert.False(h.ContainsKey("x-shashu-session-id"));
            Assert.False(h.ContainsKey("x-shashu-caller-agent-id"));
            Assert.False(h.ContainsKey("x-shashu-hop-depth"));
        }
    }

    [Fact]
    public void Caller_omitted_when_no_inbound_caller()
    {
        using (Tracer.Incoming(H(("x-shashu-trace-id", "f"), ("x-shashu-hop-depth", "1"))))
        {
            var h = WorkflowContext.Build();
            Assert.Equal("f", h["x-shashu-trace-id"]);
            Assert.Equal("2", h["x-shashu-hop-depth"]);
            Assert.False(h.ContainsKey("x-shashu-caller-agent-id"));
        }
    }

    [Fact]
    public void Session_omitted_when_not_propagated()
    {
        using (Tracer.Incoming(H(("x-shashu-trace-id", "f"), ("x-shashu-caller-agent-id", "A"))))
        {
            var h = WorkflowContext.Build();
            Assert.False(h.ContainsKey("x-shashu-session-id"));
            Assert.Equal("A", h["x-shashu-caller-agent-id"]); // forwarded invoker
            Assert.Equal("1", h["x-shashu-hop-depth"]);
        }
    }

    [Fact]
    public void Span_inside_capture_inherits_trace_and_forwards_lineage()
    {
        using (Tracer.Incoming(H(
            ("x-shashu-trace-id", "FLOW"), ("x-shashu-session-id", "S"),
            ("x-shashu-hop-depth", "1"), ("x-shashu-caller-agent-id", "A"))))
        using (Tracer.Span("work"))
        {
            var h = WorkflowContext.Build();
            Assert.Equal("FLOW", h["x-shashu-trace-id"]); // span inherits captured trace
            Assert.True(h.ContainsKey("x-shashu-span-id"));
            Assert.Equal("S", h["x-shashu-session-id"]);
            Assert.Equal("A", h["x-shashu-caller-agent-id"]); // forwarded invoker, not self
            Assert.Equal("2", h["x-shashu-hop-depth"]);
        }
    }

    // --------------------------------------------------------------------- //
    // Invoking side — BuildInvocationHeaders stamps caller = SELF (§4.1)
    // --------------------------------------------------------------------- //
    [Fact]
    public void Invocation_at_origin_stamps_caller_self()
    {
        using (Tracer.Incoming(H())) // clean origin: OutboundHopDepth = 1, no session
        {
            var h = WorkflowContext.BuildInvocationHeaders("agent-A");
            Assert.Equal("agent-A", h["x-shashu-caller-agent-id"]);
            Assert.True(h.ContainsKey("x-shashu-trace-id"));
            Assert.Equal("1", h["x-shashu-hop-depth"]);
            Assert.False(h.ContainsKey("x-shashu-session-id"));
        }
    }

    [Fact]
    public void Invocation_midflow_propagates_served_flow()
    {
        using (Tracer.Incoming(H(
            ("x-shashu-trace-id", "F"), ("x-shashu-session-id", "S"),
            ("x-shashu-hop-depth", "1"), ("x-shashu-caller-agent-id", "agent-A"))))
        {
            var h = WorkflowContext.BuildInvocationHeaders("agent-B");
            Assert.Equal("F", h["x-shashu-trace-id"]);                 // served flow propagated
            Assert.Equal("agent-B", h["x-shashu-caller-agent-id"]);    // B stamps ITSELF as invoker of C
            Assert.Equal("S", h["x-shashu-session-id"]);
            Assert.Equal("2", h["x-shashu-hop-depth"]);
        }
    }

    [Fact]
    public void Invocation_honors_explicit_overrides()
    {
        var h = WorkflowContext.BuildInvocationHeaders("me", traceId: "T", sessionId: "S", hopDepth: 5);
        Assert.Equal("T", h["x-shashu-trace-id"]);
        Assert.Equal("me", h["x-shashu-caller-agent-id"]);
        Assert.Equal("5", h["x-shashu-hop-depth"]);
        Assert.Equal("S", h["x-shashu-session-id"]);
    }

    [Fact]
    public void Invocation_throws_when_caller_empty()
    {
        Assert.Throws<ArgumentException>(() => WorkflowContext.BuildInvocationHeaders(""));
    }

    [Fact]
    public void A2A_caller_chain_end_to_end()
    {
        using (Tracer.Incoming(H())) // clean origin
        {
            // A (origin) invokes B: A stamps caller = A.
            var aToB = WorkflowContext.BuildInvocationHeaders("agent-A", traceId: "FLOW");
            Assert.Equal("agent-A", aToB["x-shashu-caller-agent-id"]);

            // B serves A's call: B's gateway call FORWARDS caller = A (the A→B edge).
            using (Tracer.Incoming(aToB))
            {
                var bGw = WorkflowContext.Build();
                Assert.Equal("FLOW", bGw["x-shashu-trace-id"]);
                Assert.Equal("agent-A", bGw["x-shashu-caller-agent-id"]);
                Assert.Equal("2", bGw["x-shashu-hop-depth"]); // inbound 1 + 1

                // B invokes C: B stamps ITSELF; C will then forward B (the B→C edge).
                var bToC = WorkflowContext.BuildInvocationHeaders("agent-B");
                Assert.Equal("agent-B", bToC["x-shashu-caller-agent-id"]);
                Assert.Equal("FLOW", bToC["x-shashu-trace-id"]); // same flow

                using (Tracer.Incoming(bToC))
                {
                    var cGw = WorkflowContext.Build();
                    Assert.Equal("agent-B", cGw["x-shashu-caller-agent-id"]); // C forwards B
                    Assert.Equal("3", cGw["x-shashu-hop-depth"]);
                }
            }
        }
    }

    // --------------------------------------------------------------------- //
    // Incoming() scopes the capture
    // --------------------------------------------------------------------- //
    [Fact]
    public void Incoming_scopes_and_restores()
    {
        using (Tracer.Incoming(H(
            ("x-shashu-trace-id", "T"), ("x-shashu-session-id", "S"),
            ("x-shashu-caller-agent-id", "A"))))
        {
            Assert.True(Tracer.IsA2aHop);
            Assert.Equal("T", WorkflowContext.Build()["x-shashu-trace-id"]);
        }
        Assert.False(Tracer.IsA2aHop); // restored → origin, no lineage leakage
    }

    // --------------------------------------------------------------------- //
    // Client self-id cache + invocation helper
    // --------------------------------------------------------------------- //
    [Fact]
    public void Client_invocation_uses_seeded_self_id()
    {
        using var client = new ShashuClient(new ShashuConfig { BaseUrl = TestGateway.Uri, ApiKey = "sk-trustgate-test", AgentId = "seed-self" });
        Assert.Equal("seed-self", client.AgentId);
        using (Tracer.Incoming(H(("x-shashu-trace-id", "F"), ("x-shashu-hop-depth", "1"))))
        {
            var h = client.BuildInvocationHeaders();
            Assert.Equal("seed-self", h["x-shashu-caller-agent-id"]); // caller = self (invoking side)
            Assert.Equal("F", h["x-shashu-trace-id"]);
            Assert.Equal("2", h["x-shashu-hop-depth"]);
        }
    }

    [Fact]
    public void Client_invocation_throws_when_self_unknown()
    {
        using var client = new ShashuClient(new ShashuConfig { BaseUrl = TestGateway.Uri, ApiKey = "sk-trustgate-test" });
        Assert.Throws<InvalidOperationException>(() => client.BuildInvocationHeaders());
    }

    [Fact]
    public async Task Gateway_call_forwards_caller_and_caches_self_id_from_response()
    {
        var sent = new Dictionary<string, string>();
        var stub = new StubHandler((req, _) =>
        {
            foreach (var hh in req.Headers) sent[hh.Key.ToLowerInvariant()] = string.Join(",", hh.Value);
            var resp = new HttpResponseMessage(HttpStatusCode.OK) { Content = OkBody() };
            resp.Headers.TryAddWithoutValidation("x-shashu-agent-id", "gw-reported");
            return Task.FromResult(resp);
        });
        using var http = new HttpClient(stub) { BaseAddress = new Uri("https://test.invalid/") };
        using var client = new ShashuClient(
            new ShashuConfig { BaseUrl = TestGateway.Uri, ApiKey = "sk-trustgate-test", AgentId = "explicit-seed" }, http);

        using (Tracer.Incoming(H(
            ("x-shashu-trace-id", "flow-A"), ("x-shashu-hop-depth", "1"),
            ("x-shashu-caller-agent-id", "upstream-A"))))
        {
            await client.Chat.Completions.CreateAsync(new ChatCompletionRequest
            {
                Model = "gpt-4o",
                Messages = new[] { new ChatMessage("user", "hi") },
            });
        }

        Assert.Equal("flow-A", sent["x-shashu-trace-id"]);              // propagated
        Assert.Equal("upstream-A", sent["x-shashu-caller-agent-id"]);   // FORWARDED, not self
        Assert.Equal("gw-reported", client.AgentId);                       // response refreshed self-id
    }

    [Fact]
    public async Task Self_id_cached_even_on_blocked_response()
    {
        var stub = new StubHandler((_, _) =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent("{\"error\":{\"message\":\"blocked\",\"type\":\"invalid_request_error\"}}"),
            };
            resp.Headers.TryAddWithoutValidation("x-shashu-agent-id", "gw-reported");
            return Task.FromResult(resp);
        });
        using var http = new HttpClient(stub) { BaseAddress = new Uri("https://test.invalid/") };
        using var client = new ShashuClient(new ShashuConfig { BaseUrl = TestGateway.Uri, ApiKey = "sk-trustgate-test" }, http);

        await Assert.ThrowsAsync<ShashuPolicyViolation>(() =>
            client.Chat.Completions.CreateAsync(new ChatCompletionRequest
            {
                Model = "gpt-4o",
                Messages = new[] { new ChatMessage("user", "hi") },
            }));
        Assert.Equal("gw-reported", client.AgentId); // seeded before the throw
    }

    [Fact]
    public async Task Origin_client_call_emits_no_lineage_even_with_self_seeded()
    {
        var sent = new Dictionary<string, string>();
        var stub = new StubHandler((req, _) =>
        {
            foreach (var hh in req.Headers) sent[hh.Key.ToLowerInvariant()] = string.Join(",", hh.Value);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = OkBody() });
        });
        using var http = new HttpClient(stub) { BaseAddress = new Uri("https://test.invalid/") };
        using var client = new ShashuClient(
            new ShashuConfig { BaseUrl = TestGateway.Uri, ApiKey = "sk-trustgate-test", AgentId = "seeded" }, http);

        using (Tracer.Incoming(H())) // origin
        {
            await client.Chat.Completions.CreateAsync(new ChatCompletionRequest
            {
                Model = "gpt-4o",
                Messages = new[] { new ChatMessage("user", "hi") },
            });
        }

        Assert.False(sent.ContainsKey("x-shashu-caller-agent-id")); // origin: no caller even when seeded
        Assert.True(sent.ContainsKey("x-shashu-session-id")); // origin still sends a session
        Assert.False(string.IsNullOrWhiteSpace(sent["x-shashu-session-id"]));
        Assert.False(sent.ContainsKey("x-shashu-hop-depth"));
    }

    private static HttpContent OkBody() =>
        JsonContent.Create(new
        {
            id = "chatcmpl-fake-1",
            @object = "chat.completion",
            created = 1_234_567_890,
            model = "gpt-4o",
            choices = new[]
            {
                new { index = 0, message = new { role = "assistant", content = "hi back" }, finish_reason = "stop" },
            },
            usage = new { prompt_tokens = 5, completion_tokens = 2, total_tokens = 7 },
        });
}
