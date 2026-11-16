using System.Text.Json.Serialization;

namespace Shashu.Audit;

/// <summary>
/// Audit-event ingestion. Maps to <c>POST /v1/audit/events</c>.
///
/// Use when you have a connector (Microsoft Purview, Salesforce Event
/// Bus, Google Workspace audit log, custom webhook) that observes
/// vendor LLM events and needs to push them through Shashu's scanner
/// pipeline for audit-only logging — verdicts feed the audit log + alerts
/// but never block the originating request (the user already saw the
/// vendor's response).
/// </summary>
public sealed class AuditResource
{
    private readonly ShashuApi _api;

    internal AuditResource(ShashuApi api) => _api = api;

    /// <summary>
    /// Submit a single vendor LLM event for audit-only scanning.
    /// </summary>
    /// <param name="request">Audit event payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The gateway's verdict — verdict / threats / pillars / severity.</returns>
    /// <example>
    /// <code>
    /// var result = await client.Audit.EventsAsync(new AuditEventRequest {
    ///     Source = "ms_copilot",
    ///     ExternalEventId = "evt_abc123",
    ///     OccurredAt = DateTimeOffset.UtcNow,
    ///     Conversation = new AuditConversation {
    ///         Messages = new[] { new AuditMessage("user", "summarize Q3 board minutes") },
    ///         Response = "Q3 minutes: ...",
    ///     },
    ///     UserContext = new Dictionary&lt;string, object?&gt; {
    ///         ["user_id"] = "alice@example.com",
    ///     },
    /// });
    /// if (result.IsThreat) { /* alert */ }
    /// </code>
    /// </example>
    public Task<AuditEventResponse> EventsAsync(
        AuditEventRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _api.PostAsync<AuditEventRequest, AuditEventResponse>(
            "/v1/audit/events", request, cancellationToken);
    }
}

/// <summary>
/// Request to <c>POST /v1/audit/events</c>. Mirrors the Python SDK's
/// <c>tg.audit.events(...)</c> kwargs.
/// </summary>
public sealed record AuditEventRequest
{
    /// <summary>
    /// Connector source: one of <c>ms_copilot</c>, <c>salesforce_einstein</c>,
    /// <c>google_workspace</c>, <c>purview_dlp</c>, <c>audit_other</c>.
    /// Determines which Shashu surface the gateway routes the conversation through.
    /// </summary>
    [JsonPropertyName("source")]
    public required string Source { get; init; }

    /// <summary>The conversation that vendor's LLM saw + emitted.</summary>
    [JsonPropertyName("conversation")]
    public required AuditConversation Conversation { get; init; }

    /// <summary>
    /// Vendor's stable event id — used for de-duplication across re-pushes
    /// of the same source event.
    /// </summary>
    [JsonPropertyName("external_event_id")]
    public string? ExternalEventId { get; init; }

    /// <summary>
    /// When the vendor LLM call originally happened. ISO-8601 string.
    /// </summary>
    [JsonPropertyName("occurred_at")]
    public DateTimeOffset? OccurredAt { get; init; }

    /// <summary>
    /// Optional user context — the gateway records this on the audit log
    /// row + uses it for per-user / per-org rollups.
    /// </summary>
    [JsonPropertyName("user_context")]
    public IDictionary<string, object?>? UserContext { get; init; }

    /// <summary>
    /// Optional model identifier — recorded for cost + provenance.
    /// </summary>
    [JsonPropertyName("model")]
    public string? Model { get; init; }
}

/// <summary>The conversation payload sent to <c>/v1/audit/events</c>.</summary>
public sealed record AuditConversation
{
    /// <summary>The user prompts + assistant turns the vendor LLM saw.</summary>
    [JsonPropertyName("messages")]
    public required IReadOnlyList<AuditMessage> Messages { get; init; }

    /// <summary>The vendor LLM's final response text.</summary>
    [JsonPropertyName("response")]
    public string? Response { get; init; }
}

/// <summary>One message in an audit conversation.</summary>
public sealed record AuditMessage(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] string Content);

/// <summary>
/// Gateway response from <c>/v1/audit/events</c>. The most-actionable
/// field is <see cref="IsThreat"/>; downstream alerting fires on it.
/// </summary>
public sealed record AuditEventResponse
{
    /// <summary>Internal id assigned by the gateway.</summary>
    [JsonPropertyName("event_id")]
    public string? EventId { get; init; }

    /// <summary>Verdict: <c>ALLOW</c> / <c>WOULD_BLOCK</c> / <c>FLAG</c> / <c>CLEAN</c>.</summary>
    [JsonPropertyName("verdict")]
    public string? Verdict { get; init; }

    /// <summary>
    /// True if any scanner flagged the conversation. Drive alerting off this.
    /// </summary>
    [JsonPropertyName("is_threat")]
    public bool IsThreat { get; init; }

    /// <summary>List of detected threat types (e.g. prompt-injection / PII).</summary>
    [JsonPropertyName("threats")]
    public IReadOnlyList<string>? Threats { get; init; }

    /// <summary>List of triggered Shashu pillars.</summary>
    [JsonPropertyName("pillars")]
    public IReadOnlyList<string>? Pillars { get; init; }

    /// <summary>Severity if any: <c>LOW</c> / <c>MEDIUM</c> / <c>HIGH</c> / <c>CRITICAL</c>.</summary>
    [JsonPropertyName("severity")]
    public string? Severity { get; init; }

    /// <summary>Free-form extension fields the gateway may emit.</summary>
    [JsonExtensionData]
    public IDictionary<string, System.Text.Json.JsonElement>? ExtraFields { get; init; }
}
