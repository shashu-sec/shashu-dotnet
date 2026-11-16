using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Shashu;
using Shashu.Chat;
using Shashu.Context;
using Xunit;
using Shashu.Tests.Harness;

namespace Shashu.Tests;

/// <summary>
/// Verifies <see cref="WorkflowContext"/> emits the right
/// <c>x-trustgate-*</c> headers for each Shadow AI runtime
/// (n8n / GitHub Actions / GitLab CI / local), and that the SDK's
/// request plumbing applies them to every outbound call.
///
/// <para>These tests mutate process-wide environment variables.
/// Important: they restore prior state in <c>finally</c> blocks; tests
/// are not parallelised on the same env keys.</para>
/// </summary>
public class WorkflowContextTests
{
    private static readonly string[] _envKeys =
    {
        "N8N_EXECUTION_ID", "N8N_NODE_NAME", "N8N_NODE_ID", "N8N_WORKFLOW_NAME", "N8N_WORKFLOW_ID",
        "GITHUB_ACTIONS", "GITHUB_RUN_ID", "GITHUB_WORKFLOW",
        "GITLAB_CI", "CI_PIPELINE_ID", "CI_JOB_NAME", "CI_PIPELINE_SOURCE",
    };

    private static IDisposable SetEnv(params (string key, string? value)[] kvs)
    {
        var prior = _envKeys.ToDictionary(k => k, Environment.GetEnvironmentVariable);
        // Clear all before applying overrides — tests must not inherit
        // ambient state from the host (CI, dev box) that runs them.
        foreach (var k in _envKeys) Environment.SetEnvironmentVariable(k, null);
        foreach (var (k, v) in kvs) Environment.SetEnvironmentVariable(k, v);
        return new RestoreEnv(prior);
    }

    private sealed class RestoreEnv : IDisposable
    {
        private readonly Dictionary<string, string?> _prior;
        public RestoreEnv(Dictionary<string, string?> prior) => _prior = prior;
        public void Dispose()
        {
            foreach (var (k, v) in _prior) Environment.SetEnvironmentVariable(k, v);
        }
    }

    [Fact]
    public void Detects_local_script_when_no_managed_env_is_set()
    {
        using var env = SetEnv();
        Assert.Equal("local_script", WorkflowContext.DetectSource());
    }

    [Fact]
    public void Detects_n8n_when_N8N_EXECUTION_ID_is_set()
    {
        using var env = SetEnv(("N8N_EXECUTION_ID", "exec-12345"));
        Assert.Equal("n8n", WorkflowContext.DetectSource());

        var headers = WorkflowContext.Build();
        Assert.Equal("n8n", headers["x-trustgate-source"]);
        Assert.Equal("exec-12345", headers["x-trustgate-trace-id"]);
    }

    [Fact]
    public void Detects_github_actions_when_GITHUB_ACTIONS_is_true()
    {
        using var env = SetEnv(
            ("GITHUB_ACTIONS", "true"),
            ("GITHUB_RUN_ID", "987654321"),
            ("GITHUB_WORKFLOW", "build"));
        var headers = WorkflowContext.Build();
        Assert.Equal("github_actions", headers["x-trustgate-source"]);
        Assert.Equal("987654321", headers["x-trustgate-trace-id"]);
        Assert.Equal("build", headers["x-trustgate-workflow-name"]);
    }

    [Fact]
    public void Detects_gitlab_ci_when_GITLAB_CI_is_true()
    {
        using var env = SetEnv(
            ("GITLAB_CI", "true"),
            ("CI_PIPELINE_ID", "pipeline-42"),
            ("CI_JOB_NAME", "deploy"));
        var headers = WorkflowContext.Build();
        Assert.Equal("gitlab_ci", headers["x-trustgate-source"]);
        Assert.Equal("pipeline-42", headers["x-trustgate-trace-id"]);
        Assert.Equal("deploy", headers["x-trustgate-workflow-name"]);
    }

    [Fact]
    public void Detection_priority_n8n_wins_over_other_envs()
    {
        // Customer might have N8N + GITHUB_ACTIONS both set (e.g. n8n
        // running inside a GitHub Action). Python SDK: n8n wins.
        using var env = SetEnv(
            ("N8N_EXECUTION_ID", "n8n-exec"),
            ("GITHUB_ACTIONS", "true"),
            ("GITLAB_CI", "true"));
        Assert.Equal("n8n", WorkflowContext.DetectSource());
    }

    [Fact]
    public void Workflow_step_resolves_from_N8N_NODE_NAME()
    {
        using var env = SetEnv(
            ("N8N_EXECUTION_ID", "exec-1"),
            ("N8N_NODE_NAME", "Data_Extraction"));
        var headers = WorkflowContext.Build();
        Assert.Equal("Data_Extraction", headers["x-trustgate-workflow-step"]);
    }

    [Fact]
    public void Caller_supplied_workflow_step_wins_over_N8N_auto_detection()
    {
        using var env = SetEnv(
            ("N8N_EXECUTION_ID", "exec-1"),
            ("N8N_NODE_NAME", "auto_detected"));
        var headers = WorkflowContext.Build(workflowStep: "Caller_Override");
        Assert.Equal("Caller_Override", headers["x-trustgate-workflow-step"]);
    }

    [Fact]
    public void Local_script_emits_fresh_trace_id_per_call()
    {
        using var env = SetEnv();
        var first = WorkflowContext.Build()["x-trustgate-trace-id"];
        var second = WorkflowContext.Build()["x-trustgate-trace-id"];
        Assert.NotEqual(first, second);
        // Trace ids should look like a 32-char GUID (no dashes) in this SDK.
        Assert.Equal(32, first.Length);
    }

    [Fact]
    public void Source_tool_blob_is_valid_json_with_sdk_identification()
    {
        using var env = SetEnv();
        var headers = WorkflowContext.Build();
        var blob = headers["x-trustgate-source-tool"];
        using var doc = JsonDocument.Parse(blob);
        Assert.Equal("shashu-dotnet", doc.RootElement.GetProperty("sdk").GetString());
        Assert.Equal("dotnet", doc.RootElement.GetProperty("language").GetString());
        Assert.True(doc.RootElement.TryGetProperty("timestamp_utc", out _));
    }

    [Fact]
    public async Task Headers_applied_to_every_outbound_request_through_the_SDK()
    {
        // End-to-end: build a ShashuClient, make a chat completion call,
        // and verify the workflow-context headers landed on the wire.
        using var env = SetEnv(
            ("N8N_EXECUTION_ID", "exec-from-test"),
            ("N8N_NODE_NAME", "test_step"));

        var capture = new HeaderCapturingHandler();
        using var http = new HttpClient(capture) { BaseAddress = new Uri("https://test.invalid/") };
        using var client = new ShashuClient(
            new ShashuConfig { BaseUrl = TestGateway.Uri, ApiKey = "sk-trustgate-test" }, http);

        await client.Chat.Completions.CreateAsync(new ChatCompletionRequest
        {
            Model = "gpt-4o",
            Messages = new[] { new ChatMessage("user", "hi") },
        });

        Assert.NotNull(capture.LastRequest);
        Assert.Equal("n8n", capture.LastRequest!.Headers.GetValues("x-trustgate-source").Single());
        Assert.Equal("exec-from-test", capture.LastRequest.Headers.GetValues("x-trustgate-trace-id").Single());
        Assert.Equal("test_step", capture.LastRequest.Headers.GetValues("x-trustgate-workflow-step").Single());
        Assert.True(capture.LastRequest.Headers.Contains("x-trustgate-source-tool"));
    }
}

/// <summary>Captures the request and returns a minimal valid response.</summary>
internal sealed class HeaderCapturingHandler : HttpMessageHandler
{
    public HttpRequestMessage? LastRequest { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new
            {
                id = "fake-1",
                @object = "chat.completion",
                created = 1,
                model = "gpt-4o",
                choices = new[]
                {
                    new
                    {
                        index = 0,
                        message = new { role = "assistant", content = "ok" },
                        finish_reason = "stop",
                    },
                },
                usage = new { prompt_tokens = 1, completion_tokens = 1, total_tokens = 2 },
            }),
        });
    }
}
