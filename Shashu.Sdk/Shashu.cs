using System.Text.Json;
using Shashu.Chat;

namespace Shashu;

/// <summary>
/// Decision reading and block recognition. Names from
/// <c>contracts/sdk-surface.v1.md</c>: <c>DecisionOf</c>, <c>IsBlock</c>,
/// <c>DecisionFrom</c>.
/// </summary>
/// <remarks>
/// Absence returns nothing — not a synthesised <c>allow</c>, not a
/// <c>fail</c>. An unrecognised <c>decision</c> is a block. No copy of the
/// <c>code</c> vocabulary is held; block-ness is carried by
/// <c>decision</c> alone.
/// </remarks>
public static class Shashu
{
    /// <summary>Gateway code: no identity provider is set up for agents.</summary>
    public const string PassProviderNotSetUp = CredentialResolver.PASS_PROVIDER_NOT_SET_UP;
    /// <summary>Gateway code: pass sign-in needs the Enterprise plan.</summary>
    public const string PassNotLicensed = CredentialResolver.PASS_NOT_LICENSED;
    /// <summary>Gateway code: this pass has expired.</summary>
    public const string PassExpired = CredentialResolver.PASS_EXPIRED;
    /// <summary>Gateway code: this pass is meant for a different audience.</summary>
    public const string PassWrongAudience = CredentialResolver.PASS_WRONG_AUDIENCE;
    /// <summary>Gateway code: this pass comes from an unknown issuer.</summary>
    public const string PassWrongIssuer = CredentialResolver.PASS_WRONG_ISSUER;
    /// <summary>Gateway code: no agent matches this pass.</summary>
    public const string PassUnknownAgent = CredentialResolver.PASS_UNKNOWN_AGENT;
    /// <summary>The six <c>pass_*</c> codes, in the order the table lists them.</summary>
    public static readonly IReadOnlyList<string> PassCodes = CredentialResolver.PASS_CODES;
    /// <summary>
    /// Decisions where the caller holds the provider's content.
    /// Everything else — including an unrecognised value — is a block.
    /// </summary>
    private static readonly HashSet<string> Delivered = new(StringComparer.Ordinal)
    {
        "allow", "allow_altered", "not_checked",
    };

    /// <summary>The six §2 decisions. Frozen; the gateway's CI fails a seventh.</summary>
    private static readonly HashSet<string> Decisions = new(StringComparer.Ordinal)
    {
        "allow", "allow_altered", "not_checked", "block", "refuse", "fail",
    };

    /// <summary>The one decision <see cref="IsBlock"/> is about.</summary>
    private const string Block = "block";

    /// <summary>
    /// A wire value reduced to a known decision, failing CLOSED.
    ///
    /// <para>Mirrors Python's <c>_coerce_decision</c> and the gateway's
    /// <c>envelope.decision_of</c>: anything this cannot positively recognise
    /// — an unknown string, a null — is a block. Coercing BEFORE the comparison
    /// is what lets <see cref="IsBlock"/> be a bare equality on <c>block</c>
    /// and still fail closed on a decision invented after this package
    /// shipped.</para>
    /// </summary>
    private static string CoerceDecision(string? raw)
        => raw is not null && Decisions.Contains(raw) ? raw : Block;

    /// <summary>
    /// Read the decision off whatever the caller is holding. Absence
    /// returns <c>null</c>.
    /// </summary>
    public static ShashuDecision? DecisionOf(object? resultOrError)
    {
        if (resultOrError is null)
            return null;
        if (resultOrError is ShashuDecision already)
            return already;
        if (resultOrError is ChatCompletionResponse chat)
            return DecisionFrom(chat, chat.ShashuHeaders);
        if (resultOrError is ShashuException ex)
            return DecisionFrom(ex.ResponseBody, ex.ResponseHeaders);
        return DecisionFrom(resultOrError, headers: null);
    }

    /// <summary>
    /// True when the caller is holding a Shashu content BLOCK. Inspects;
    /// never patches or wraps. Absence is <c>false</c>. A decision this method
    /// cannot fully parse — or an unrecognised <c>decision</c> — is
    /// <c>true</c> (fail closed). Reads <c>decision</c> only.
    ///
    /// <para><b>It means <c>decision == "block"</c>, and nothing else.</b>
    /// Until 2026-09-17 it was <c>!Delivered.Contains(decision)</c>, so a
    /// <c>refuse</c> (a policy refusal — a missing API key, a disabled
    /// feature, an address we do not serve) and a <c>fail</c> (we broke;
    /// nobody decided) both answered <c>true</c>. That truthfully answers a
    /// different and useful question — "did I get content at all" — but under
    /// THIS name it tells a customer refused on policy that a guardrail fired
    /// on their content, which sends them to review a prompt nothing
    /// inspected. Python and Node have always been <c>decision == "block"</c>;
    /// this is the reconciliation. No "was it delivered" accessor is added
    /// here.</para>
    /// </summary>
    public static bool IsBlock(object? err)
    {
        if (err is null)
            return false;

        var probe = Probe(err);
        if (probe.Kind == ProbeKind.Absent)
            return false;
        if (probe.Kind == ProbeKind.Unparseable)
            return true;
        return CoerceDecision(probe.Decision) == Block;
    }

    /// <summary>
    /// Parse the two plain carriers. Either argument may be absent.
    /// Absence in both returns <c>null</c>.
    /// </summary>
    public static ShashuDecision? DecisionFrom(object? body, IEnumerable<KeyValuePair<string, string>>? headers)
    {
        var fromBody = ReadBody(body);
        var fromHeaders = ReadHeaders(headers);
        if (fromBody is not null)
            return fromBody;
        return fromHeaders;
    }

    private enum ProbeKind { Absent, Unparseable, Present }

    private readonly struct ProbeResult
    {
        public ProbeKind Kind { get; init; }
        public string? Decision { get; init; }
    }

    private static ProbeResult Probe(object err)
    {
        if (err is ShashuContentFilterException cf)
        {
            var parsed = DecisionFrom(cf.ResponseBody, cf.ResponseHeaders);
            if (parsed is not null)
                return new ProbeResult { Kind = ProbeKind.Present, Decision = parsed.Decision };
            return new ProbeResult { Kind = ProbeKind.Unparseable };
        }

        var decision = DecisionOf(err);
        if (decision is not null)
            return new ProbeResult { Kind = ProbeKind.Present, Decision = decision.Decision };

        if (CarrierPresentButUnreadable(err))
            return new ProbeResult { Kind = ProbeKind.Unparseable };

        return new ProbeResult { Kind = ProbeKind.Absent };
    }

    private static bool CarrierPresentButUnreadable(object err)
    {
        if (err is ChatCompletionResponse chat
            && chat.ExtraFields is not null
            && chat.ExtraFields.ContainsKey("trustgate"))
        {
            return true;
        }
        if (err is ShashuException ex && EnvelopeObjectPresent(ex.ResponseBody))
            return true;
        if (err is string s && EnvelopeObjectPresent(s))
            return true;
        return false;
    }

    private static bool EnvelopeObjectPresent(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return false;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return LocateEnvelope(doc.RootElement) is { ValueKind: JsonValueKind.Object };
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static ShashuDecision? ReadBody(object? body)
    {
        if (body is null)
            return null;
        if (body is ShashuDecision d)
            return d;
        if (body is ChatCompletionResponse chat)
            return ReadFromExtra(chat.ExtraFields);
        if (body is JsonElement el)
            return ReadFromElement(el);
        if (body is JsonDocument doc)
            return ReadFromElement(doc.RootElement);
        if (body is string s)
        {
            if (string.IsNullOrWhiteSpace(s))
                return null;
            try
            {
                using var parsed = JsonDocument.Parse(s);
                return ReadFromElement(parsed.RootElement);
            }
            catch (JsonException)
            {
                return null;
            }
        }
        return null;
    }

    private static ShashuDecision? ReadFromExtra(Dictionary<string, JsonElement>? extra)
    {
        if (extra is null || !extra.TryGetValue("trustgate", out var tg))
            return null;
        return ReadEnvelope(tg);
    }

    private static ShashuDecision? ReadFromElement(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return null;
        var env = LocateEnvelope(root);
        return env is { } e ? ReadEnvelope(e) : null;
    }

    private static JsonElement? LocateEnvelope(JsonElement root)
    {
        if (root.TryGetProperty("trustgate"u8, out var top) && top.ValueKind == JsonValueKind.Object)
            return top;
        if (root.TryGetProperty("error"u8, out var err)
            && err.ValueKind == JsonValueKind.Object
            && err.TryGetProperty("trustgate"u8, out var nested)
            && nested.ValueKind == JsonValueKind.Object)
        {
            return nested;
        }
        return null;
    }

    private static ShashuDecision? ReadEnvelope(JsonElement env)
    {
        if (env.ValueKind != JsonValueKind.Object)
            return null;

        // ONE pass over the envelope, not a lookup per field: each lookup is a
        // scan, and the three optional blocks are absent from nearly every
        // response, so looking each one up would cost three full scans for
        // nothing. The last duplicate wins, as it does for a lookup.
        JsonElement decEl = default, codeEl = default, retryEl = default, idEl = default;
        JsonElement attachmentsEl = default, memoryEl = default, routePolicyEl = default, inspectionEl = default;
        foreach (var p in env.EnumerateObject())
        {
            if (p.NameEquals("decision"u8)) decEl = p.Value;
            else if (p.NameEquals("code"u8)) codeEl = p.Value;
            else if (p.NameEquals("retryable"u8)) retryEl = p.Value;
            else if (p.NameEquals("id"u8)) idEl = p.Value;
            else if (p.NameEquals("attachments"u8)) attachmentsEl = p.Value;
            else if (p.NameEquals("memory"u8)) memoryEl = p.Value;
            else if (p.NameEquals("route_policy"u8)) routePolicyEl = p.Value;
            else if (p.NameEquals("inspection"u8)) inspectionEl = p.Value;
        }

        if (decEl.ValueKind != JsonValueKind.String)
            return null;
        var decision = decEl.GetString();
        if (string.IsNullOrEmpty(decision))
            return null;

        return new ShashuDecision
        {
            Decision = decision,
            Code = codeEl.ValueKind == JsonValueKind.String ? codeEl.GetString() : null,
            Retryable = retryEl.ValueKind is JsonValueKind.True or JsonValueKind.False ? retryEl.GetBoolean() : null,
            Id = idEl.ValueKind == JsonValueKind.String ? idEl.GetString() : null,
            Attachments = DecisionBlocks.AttachmentsFromBody(attachmentsEl),
            Memory = DecisionBlocks.MemoryFromBody(memoryEl),
            RoutePolicy = DecisionBlocks.RoutePolicyFromBody(routePolicyEl),
            Inspection = DecisionBlocks.InspectionFromBody(inspectionEl),
        };
    }

    private static ShashuDecision? ReadHeaders(IEnumerable<KeyValuePair<string, string>>? headers)
    {
        if (headers is null)
            return null;
        string? decision = null;
        string? code = null;
        string? attachments = null;
        string? memory = null;
        string? inspection = null;
        foreach (var (k, v) in headers)
        {
            // The five names differ in length, so the length picks the one
            // candidate and a header can cost at most one comparison.
            switch (k.Length)
            {
                case 20 when k.Equals("x-trustgate-decision", StringComparison.OrdinalIgnoreCase)
                             && !string.IsNullOrWhiteSpace(v):
                    decision = v.Trim();
                    break;
                case 16 when k.Equals("x-trustgate-code", StringComparison.OrdinalIgnoreCase)
                             && !string.IsNullOrWhiteSpace(v):
                    code = v.Trim();
                    break;
                case 23 when k.Equals(DecisionBlocks.HeaderAttachments, StringComparison.OrdinalIgnoreCase):
                    attachments = v;
                    break;
                case 18 when k.Equals(DecisionBlocks.HeaderMemory, StringComparison.OrdinalIgnoreCase):
                    memory = v;
                    break;
                case 22 when k.Equals(DecisionBlocks.HeaderInspection, StringComparison.OrdinalIgnoreCase):
                    inspection = v;
                    break;
            }
        }
        if (decision is null)
            return null;
        // Counts only: nothing per-file and no route policy ever rides a header.
        return new ShashuDecision
        {
            Decision = decision,
            Code = code,
            Attachments = attachments is null ? null : DecisionBlocks.AttachmentsFromHeader(attachments),
            Memory = memory is null ? null : DecisionBlocks.MemoryFromHeader(memory),
            Inspection = DecisionBlocks.InspectionFromHeader(inspection),
        };
    }
}

/// <summary>
/// The decision envelope a client can read. <see cref="Decision"/> is one
/// of <c>allow</c>, <c>allow_altered</c>, <c>not_checked</c>, <c>block</c>,
/// <c>refuse</c>, <c>fail</c> — or an unrecognised value the gateway added
/// later, which <see cref="Shashu.IsBlock"/> treats as a block.
/// </summary>
public sealed class ShashuDecision
{
    /// <summary>The decision string. Not synthesised when absent.</summary>
    public required string Decision { get; init; }

    /// <summary>The finding code, when the envelope or header carried one.</summary>
    public string? Code { get; init; }

    /// <summary>Whether the caller should retry, when stated.</summary>
    public bool? Retryable { get; init; }

    /// <summary>The audit-record id, when stated.</summary>
    public string? Id { get; init; }

    /// <summary>
    /// What happened to each attached file; null when the request had none.
    /// Read off the body; off the <c>x-trustgate-attachments</c> counts when
    /// the body carried no envelope. <see cref="global::Shashu.Attachments.Carrier"/>
    /// says which.
    /// </summary>
    public Attachments? Attachments { get; init; }

    /// <summary>Memory writes and recalls; null when there were none. Same carrier rule.</summary>
    public MemoryActivity? Memory { get; init; }

    /// <summary>Why a route was refused for the data in the request; body only.</summary>
    public RoutePolicy? RoutePolicy { get; init; }

    /// <summary>
    /// What the gateway inspected: <c>input</c>, <c>output</c>, <c>reason</c> and a
    /// <c>detectors</c> map of detector name to state (e.g. <c>memory</c> →
    /// <c>checked</c> / <c>partial</c> / <c>skipped</c>). Same shape off the body or the
    /// <c>x-trustgate-inspection</c> header; read-only; states are raw strings. Null when
    /// neither carried one.
    /// </summary>
    public IReadOnlyDictionary<string, object>? Inspection { get; init; }

    /// <summary>Files the model received without a complete inspection.</summary>
    public int FilesUnverified => Attachments is null ? 0 : Attachments.NotInspected + Attachments.ScannedPartial;

    /// <summary>Files that were not forwarded.</summary>
    public int FilesBlocked => Attachments?.Blocked ?? 0;

    /// <summary>Memory writes the gateway refused to store.</summary>
    public int MemoryBlocked => Memory?.WritesBlocked ?? 0;

    /// <summary>Memory recalls the gateway flagged.</summary>
    public int MemoryRecallsFlagged => Memory?.RecallsFlagged ?? 0;

    /// <summary><c>gateway</c> / <c>observed</c> / <c>hosted</c> / <c>mixed</c>, raw; null when unknown.</summary>
    public string? MemoryCoverage => Memory?.Coverage;
}
