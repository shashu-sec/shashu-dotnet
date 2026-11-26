using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Shashu;
using Shashu.Audit;
using Xunit;
using Shashu.Tests.Harness;

namespace Shashu.Tests;

/// <summary>
/// Verifies <c>client.Audit.EventsAsync(...)</c> serialises the request
/// to <c>POST /v1/audit/events</c> in the shape the gateway expects, and
/// deserialises the verdict response correctly.
/// </summary>
public class AuditTests
{
    [Fact]
    public async Task EventsAsync_posts_to_correct_path_with_typed_request()
    {
        var capture = new AuditCapturingHandler(verdict: "WOULD_BLOCK", isThreat: true);
        using var http = new HttpClient(capture) { BaseAddress = new Uri("https://test.invalid/") };
        using var client = new ShashuClient(
            new ShashuConfig { BaseUrl = TestGateway.Uri, ApiKey = "sk-shashu-test" }, http);

        var resp = await client.Audit.EventsAsync(new AuditEventRequest
        {
            Source = "ms_copilot",
            ExternalEventId = "evt_abc123",
            OccurredAt = DateTimeOffset.Parse("2026-05-02T14:00:00Z"),
            Conversation = new AuditConversation
            {
                Messages = new[] { new AuditMessage("user", "summarize Q3 minutes") },
                Response = "Q3 minutes: ...",
            },
            UserContext = new Dictionary<string, object?>
            {
                ["user_id"] = "alice@example.com",
            },
            Model = "gpt-4o",
        });

        // Verify the request shape
        Assert.NotNull(capture.LastRequestBody);
        Assert.Equal("/v1/audit/events", capture.LastPath);
        using var body = JsonDocument.Parse(capture.LastRequestBody!);
        Assert.Equal("ms_copilot", body.RootElement.GetProperty("source").GetString());
        Assert.Equal("evt_abc123", body.RootElement.GetProperty("external_event_id").GetString());
        Assert.Equal("gpt-4o", body.RootElement.GetProperty("model").GetString());
        Assert.Equal("alice@example.com",
            body.RootElement.GetProperty("user_context").GetProperty("user_id").GetString());
        Assert.Equal("user",
            body.RootElement.GetProperty("conversation").GetProperty("messages")[0].GetProperty("role").GetString());

        // Verify the response is deserialised
        Assert.Equal("WOULD_BLOCK", resp.Verdict);
        Assert.True(resp.IsThreat);
    }

    [Fact]
    public async Task EventsAsync_omits_optional_fields_when_unset()
    {
        // Gateway treats explicit null vs absent differently for some
        // metadata fields; make sure we omit cleanly when unset.
        var capture = new AuditCapturingHandler(verdict: "CLEAN", isThreat: false);
        using var http = new HttpClient(capture) { BaseAddress = new Uri("https://test.invalid/") };
        using var client = new ShashuClient(
            new ShashuConfig { BaseUrl = TestGateway.Uri, ApiKey = "sk-shashu-test" }, http);

        await client.Audit.EventsAsync(new AuditEventRequest
        {
            Source = "audit_other",
            Conversation = new AuditConversation
            {
                Messages = new[] { new AuditMessage("user", "hi") },
            },
            // No external_event_id, occurred_at, user_context, model, response
        });

        using var body = JsonDocument.Parse(capture.LastRequestBody!);
        Assert.False(body.RootElement.TryGetProperty("external_event_id", out _));
        Assert.False(body.RootElement.TryGetProperty("occurred_at", out _));
        Assert.False(body.RootElement.TryGetProperty("user_context", out _));
        Assert.False(body.RootElement.TryGetProperty("model", out _));
    }

    [Fact]
    public async Task EventsAsync_403_throws_ShashuPolicyViolation_with_parsed_message()
    {
        var capture = new AuditCapturingHandler(
            statusOverride: HttpStatusCode.Forbidden,
            bodyOverride: "{\"error\":{\"message\":\"Vendor source not configured for this tenant\"}}");
        using var http = new HttpClient(capture) { BaseAddress = new Uri("https://test.invalid/") };
        using var client = new ShashuClient(
            new ShashuConfig { BaseUrl = TestGateway.Uri, ApiKey = "sk-shashu-test", MaxRetries = 0 }, http);

        var ex = await Assert.ThrowsAsync<ShashuPolicyViolation>(() =>
            client.Audit.EventsAsync(new AuditEventRequest
            {
                Source = "ms_copilot",
                Conversation = new AuditConversation
                {
                    Messages = new[] { new AuditMessage("user", "x") },
                },
            }));

        Assert.Equal("Vendor source not configured for this tenant", ex.Message);
    }
}

/// <summary>Captures audit POST bodies + lets the test override the response.</summary>
internal sealed class AuditCapturingHandler : HttpMessageHandler
{
    public string? LastRequestBody { get; private set; }
    public string? LastPath { get; private set; }

    private readonly string _verdict;
    private readonly bool _isThreat;
    private readonly HttpStatusCode _statusOverride;
    private readonly string? _bodyOverride;

    public AuditCapturingHandler(string verdict = "CLEAN", bool isThreat = false,
                                 HttpStatusCode statusOverride = HttpStatusCode.OK,
                                 string? bodyOverride = null)
    {
        _verdict = verdict;
        _isThreat = isThreat;
        _statusOverride = statusOverride;
        _bodyOverride = bodyOverride;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastPath = request.RequestUri?.AbsolutePath;
        if (request.Content is not null)
            LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (_bodyOverride is not null)
        {
            return new HttpResponseMessage(_statusOverride)
            {
                Content = new StringContent(_bodyOverride, System.Text.Encoding.UTF8, "application/json"),
            };
        }

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new
            {
                event_id = "audit-evt-1",
                verdict = _verdict,
                is_threat = _isThreat,
                // Placeholder, not a contract: no test asserts `threats`. The
                // value comes from the §3 code vocabulary
                // (app/contract/envelope.py::CODES); `dlp` is chosen because it
                // cannot be mistaken for the pre-contract pipeline threat_type
                // names (INJECTION, Shashu/ingress-text, ...) that the wire
                // stopped carrying on 2026-09-03.
                threats = new[] { "dlp" },
                pillars = new[] { "content" },
                severity = "MEDIUM",
            }),
        };
    }
}
