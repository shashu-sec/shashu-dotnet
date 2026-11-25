using System.Runtime.InteropServices;
using System.Text.Json;
using Shashu.Tracing;

namespace Shashu.Context;

/// <summary>
/// Builds the <c>x-shashu-*</c> + <c>x-trustlog-*</c> headers that
/// give the gateway Shadow AI attribution and runtime context for every
/// request. The Python SDK calls the same logic
/// <c>build_trustgate_headers()</c>; we keep the produced header map
/// shape identical for cross-SDK parity.
/// </summary>
/// <remarks>
/// <para>What gets emitted:</para>
/// <list type="bullet">
///   <item><c>x-shashu-source</c> — one of <c>n8n</c>, <c>github_actions</c>,
///         <c>gitlab_ci</c>, <c>local_script</c>. Drives the gateway's
///         Shadow AI source-tool taxonomy.</item>
///   <item><c>x-shashu-trace-id</c> — the workflow's trace id. Comes from
///         <c>N8N_EXECUTION_ID</c> / <c>GITHUB_RUN_ID</c> / <c>CI_PIPELINE_ID</c>
///         when running in a managed environment; otherwise a fresh GUID per
///         build.</item>
///   <item><c>x-shashu-workflow-name</c> — set when running inside a
///         managed workflow (n8n workflow name, GitHub workflow name, GitLab
///         job name).</item>
///   <item><c>x-shashu-workflow-step</c> — caller can pass an explicit
///         step (e.g. <c>"Data_Extraction"</c>) to drive the Gantt;
///         auto-detected from <c>N8N_NODE_NAME</c> / <c>N8N_NODE_ID</c>.</item>
///   <item><c>x-trustlog-os</c>, <c>x-trustlog-arch</c>,
///         <c>x-trustlog-runtime-version</c> — runtime metadata for the
///         gateway's deployment + capacity reports.</item>
///   <item><c>x-shashu-source-tool</c> — JSON blob describing the
///         calling SDK + version + system info. Free-form by design so we
///         can extend without breaking the contract.</item>
/// </list>
/// </remarks>
public static class WorkflowContext
{
    /// <summary>
    /// Build the per-request header map. Static, allocates a fresh
    /// dictionary on every call — the values include a per-call timestamp
    /// (in the <c>source-tool</c> blob) and possibly a fresh GUID trace id
    /// when no managed-workflow trace exists.
    /// </summary>
    /// <param name="workflowStep">Optional explicit workflow step. Wins over auto-detection.</param>
    /// <param name="sourceToolOverrides">Extra fields merged into the <c>x-shashu-source-tool</c> JSON blob.</param>
    /// <param name="sessionId">
    /// Session to stamp. The caller resolved precedence; this method does
    /// not mint. Emitted whenever non-empty, at origin and mid-flow.
    /// </param>
    public static Dictionary<string, string> Build(
        string? workflowStep = null,
        IReadOnlyDictionary<string, object?>? sourceToolOverrides = null,
        string? sessionId = null)
    {
        var source = DetectSource();
        var traceId = ResolveTraceId();
        var workflowName = ResolveWorkflowName();
        var step = workflowStep ?? ResolveN8NWorkflowStep();

        var headers = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["x-shashu-source"] = source,
            ["x-shashu-trace-id"] = traceId,
            ["x-trustlog-os"] = RuntimeInformation.OSDescription,
            ["x-trustlog-arch"] = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
            ["x-trustlog-runtime-version"] = Environment.Version.ToString(),
        };

        if (!string.IsNullOrEmpty(workflowName))
            headers["x-shashu-workflow-name"] = workflowName;

        if (!string.IsNullOrEmpty(step))
            headers["x-shashu-workflow-step"] = step!;

        // Source-tool metadata: SDK identification + system info. Same shape
        // as Python's get_source_tool_metadata().
        var metadata = new Dictionary<string, object?>
        {
            ["sdk"] = "shashu-dotnet",
            ["sdk_version"] = ShashuClient.SdkVersion,
            ["language"] = "dotnet",
            ["language_version"] = Environment.Version.ToString(),
            ["os"] = RuntimeInformation.OSDescription,
            ["arch"] = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
            ["timestamp_utc"] = DateTimeOffset.UtcNow.ToString("o"),
        };
        if (sourceToolOverrides is not null)
        {
            foreach (var kv in sourceToolOverrides)
                metadata[kv.Key] = kv.Value;
        }
        headers["x-shashu-source-tool"] = JsonSerializer.Serialize(metadata);

        // Tracer integration: when a span is active, OVERRIDE the
        // workflow's trace id with the span's id and emit span/parent
        // headers so the gateway can build hierarchy. Mirrors the
        // Python SDK's contract.
        if (Tracer.IsActive && Tracer.TraceId is { } activeTrace)
        {
            headers["x-shashu-trace-id"] = activeTrace;
            headers["x-shashu-span-id"] = Tracer.CurrentSpanId ?? string.Empty;
            headers["x-shashu-parent-id"] = Tracer.ParentSpanId ?? string.Empty;
        }
        else if (Tracer.IsA2aHop && Tracer.TraceId is { } flowTrace)
        {
            // Mid-flow without a span: PROPAGATE the captured trace (do NOT mint a new one).
            headers["x-shashu-trace-id"] = flowTrace;
        }

        // A2A OUTBOUND PROPAGATION (WP-A2A-4 / ADR-007 §0, §4.1 LOCKED). Only mid-flow
        // (an inbound flow was captured) do we attach the cross-agent lineage headers; at
        // origin the header set is unchanged, so non-A2A users are unaffected.
        //   - session-id:      the captured session, propagated so all hops share it.
        //   - caller-agent-id: the FORWARDED inbound caller (the upstream INVOKER), NEVER
        //                      this agent's own id. Origin / caller-stripped → omitted, so
        //                      the gateway records (invoker → this agent), not a self-edge.
        //                      To stamp caller = self when INVOKING a downstream agent, use
        //                      BuildInvocationHeaders (the invoking side).
        //   - hop-depth:       inbound + 1 (advisory; the gateway recounts authoritatively).
        // Session: emit whenever we have one — origin and A2A. The caller
        // resolved precedence (per-request instruction, captured inbound,
        // construction-time default). Build() itself does not mint.
        var session = sessionId ?? (Tracer.IsA2aHop ? Tracer.SessionId : null);
        if (!string.IsNullOrEmpty(session))
            headers["x-shashu-session-id"] = session;

        if (Tracer.IsA2aHop)
        {
            if (Tracer.InboundCallerAgentId is { } inboundCaller)
                headers["x-shashu-caller-agent-id"] = inboundCaller;
            headers["x-shashu-hop-depth"] =
                Tracer.OutboundHopDepth.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return headers;
    }

    /// <summary>
    /// Build the Shashu lineage headers to stamp on an INTER-AGENT INVOCATION — the
    /// message THIS agent sends to a DOWNSTREAM agent it is calling (the invoking side,
    /// ADR-007 §4.1 locked). The downstream agent then calls <c>Tracer.CaptureIncoming</c> on
    /// these headers and FORWARDS this caller on its own gateway call — forming the
    /// (this agent → downstream agent) edge.
    /// <para>
    /// <paramref name="callerAgentId"/> is THIS agent's own id (the invoker stamps itself) —
    /// the one place the SDK sets caller = self. It is advisory lineage, NEVER an auth
    /// credential; authenticate to the downstream service by its own means.
    /// </para>
    /// Defaults pull from the tracer so the served flow is propagated: <paramref name="traceId"/>
    /// → <c>Tracer.TraceId</c> (a fresh root is minted at origin, the captured flow is
    /// propagated mid-flow); <paramref name="hopDepth"/> → <c>Tracer.OutboundHopDepth</c>;
    /// <paramref name="sessionId"/> → the captured session.
    /// </summary>
    public static Dictionary<string, string> BuildInvocationHeaders(
        string callerAgentId,
        string? traceId = null,
        string? sessionId = null,
        int? hopDepth = null)
    {
        if (string.IsNullOrEmpty(callerAgentId))
            throw new ArgumentException("callerAgentId is required (this agent's own id).", nameof(callerAgentId));

        var trace = traceId ?? Tracer.TraceId ?? Guid.NewGuid().ToString("N");
        var hop = hopDepth ?? Tracer.OutboundHopDepth;
        var session = sessionId ?? Tracer.SessionId;

        var headers = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["x-shashu-trace-id"] = trace,
            ["x-shashu-caller-agent-id"] = callerAgentId,
            ["x-shashu-hop-depth"] = hop.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        if (!string.IsNullOrEmpty(session))
            headers["x-shashu-session-id"] = session!;
        return headers;
    }

    /// <summary>
    /// Detect the Shadow AI source from environment variables. Mirrors
    /// the Python SDK's <c>_detect_source()</c>: the order of checks
    /// matters — n8n &gt; github_actions &gt; gitlab_ci &gt; local_script.
    /// </summary>
    public static string DetectSource()
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("N8N_EXECUTION_ID")))
            return "n8n";
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true")
            return "github_actions";
        if (Environment.GetEnvironmentVariable("GITLAB_CI") == "true")
            return "gitlab_ci";
        return "local_script";
    }

    private static string ResolveTraceId()
    {
        var n8n = Environment.GetEnvironmentVariable("N8N_EXECUTION_ID");
        if (!string.IsNullOrEmpty(n8n)) return n8n;

        var github = Environment.GetEnvironmentVariable("GITHUB_RUN_ID");
        if (!string.IsNullOrEmpty(github)) return github;

        if (Environment.GetEnvironmentVariable("GITLAB_CI") == "true")
        {
            var pipelineId = Environment.GetEnvironmentVariable("CI_PIPELINE_ID");
            if (!string.IsNullOrEmpty(pipelineId)) return pipelineId;
        }

        // Local: fresh GUID per call. Customer using the tracer module gets
        // their span's trace id stamped over this — see Tracer in #4.D.
        return Guid.NewGuid().ToString("N");
    }

    private static string? ResolveWorkflowName()
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("N8N_EXECUTION_ID")))
        {
            return Environment.GetEnvironmentVariable("N8N_WORKFLOW_NAME")
                ?? Environment.GetEnvironmentVariable("N8N_WORKFLOW_ID")
                ?? "n8n";
        }
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true")
        {
            return Environment.GetEnvironmentVariable("GITHUB_WORKFLOW") ?? "github_actions";
        }
        if (Environment.GetEnvironmentVariable("GITLAB_CI") == "true")
        {
            return Environment.GetEnvironmentVariable("CI_JOB_NAME")
                ?? Environment.GetEnvironmentVariable("CI_PIPELINE_SOURCE")
                ?? "gitlab_ci";
        }
        return null;
    }

    private static string? ResolveN8NWorkflowStep()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("N8N_EXECUTION_ID")))
            return null;
        return Environment.GetEnvironmentVariable("N8N_NODE_NAME")
            ?? Environment.GetEnvironmentVariable("N8N_NODE_ID");
    }
}
