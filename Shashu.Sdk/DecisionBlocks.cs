using System.Collections.ObjectModel;
using System.Text.Json;

namespace Shashu;

/// <summary>
/// What happened to one attached file. A closed set on the gateway
/// (<c>contracts/decision-envelope.v1.md</c>, contract <c>2026-10-01</c>).
/// </summary>
/// <remarks>
/// A value this SDK does not know is <see cref="Unknown"/>, never an
/// exception; the wire string stays on <see cref="Attachment.RawState"/>.
/// </remarks>
public enum AttachmentState
{
    /// <summary>A state this SDK does not know, or none at all.</summary>
    Unknown = 0,
    /// <summary><c>scanned</c> — fully inspected.</summary>
    Scanned,
    /// <summary><c>scanned_partial</c> — inspected up to a cap; the rest was not.</summary>
    ScannedPartial,
    /// <summary><c>not_inspected</c> — the model received it uninspected.</summary>
    NotInspected,
    /// <summary><c>blocked</c> — not forwarded.</summary>
    Blocked,
}

/// <summary>One finding inside an attached file. Every field is passed through raw.</summary>
public sealed record AttachmentFinding
{
    /// <summary>The detector that found it. An open set.</summary>
    public string? Detector { get; init; }
    /// <summary>The finding's category. An open set.</summary>
    public string? Category { get; init; }
    /// <summary>The finding's reason code, raw. Never branch on it.</summary>
    public string? Code { get; init; }
    /// <summary>Severity, raw.</summary>
    public string? Severity { get; init; }
    /// <summary>Where in the file, e.g. <c>page[1]</c>.</summary>
    public string? Locator { get; init; }
}

/// <summary>One attached file, as the gateway reported it. Read-only.</summary>
public sealed record Attachment
{
    /// <summary>The state; <see cref="AttachmentState.Unknown"/> for a value this SDK does not know.</summary>
    public AttachmentState State { get; init; }
    /// <summary>The verbatim wire value of the state, known or not.</summary>
    public string? RawState { get; init; }
    /// <summary>Why, raw: a reason this SDK has never heard of is still just a string.</summary>
    public string? Reason { get; init; }
    /// <summary>The file name the request gave.</summary>
    public string? Filename { get; init; }
    /// <summary>The media type the request declared.</summary>
    public string? DeclaredType { get; init; }
    /// <summary>The media type the bytes matched.</summary>
    public string? SniffedType { get; init; }
    /// <summary>Hex sha256 of the bytes.</summary>
    public string? Sha256 { get; init; }
    /// <summary>Size in bytes.</summary>
    public long? Bytes { get; init; }
    /// <summary>Where it came from, e.g. <c>inline</c>.</summary>
    public string? Source { get; init; }
    /// <summary>Position among the request's attachments.</summary>
    public int? Index { get; init; }
    /// <summary>Where in the request, e.g. <c>messages[0].content[1]</c>.</summary>
    public string? Path { get; init; }
    /// <summary>Findings inside the file. Empty when none.</summary>
    public IReadOnlyList<AttachmentFinding> Findings { get; init; } = Array.Empty<AttachmentFinding>();
}

/// <summary>
/// The decision's <c>attachments</c> block, or its header counts when that is
/// all there is.
/// </summary>
public sealed record Attachments
{
    /// <summary>Files on the request.</summary>
    public int Total { get; init; }
    /// <summary>Fully inspected.</summary>
    public int Scanned { get; init; }
    /// <summary>Inspected up to a cap.</summary>
    public int ScannedPartial { get; init; }
    /// <summary>Received uninspected.</summary>
    public int NotInspected { get; init; }
    /// <summary>Not forwarded.</summary>
    public int Blocked { get; init; }
    /// <summary>Per-file detail. Empty when read off the header, which never carries it.</summary>
    public IReadOnlyList<Attachment> Items { get; init; } = Array.Empty<Attachment>();
    /// <summary><c>"body"</c> or <c>"header"</c> — which placement these came from.</summary>
    public string Carrier { get; init; } = "body";
}

/// <summary>One memory write or recall. Every field is passed through raw.</summary>
public sealed record MemoryItem
{
    /// <summary><c>write</c> or <c>recall</c> today; a new kind is still just a string.</summary>
    public string? Kind { get; init; }
    /// <summary>What happened to it, raw.</summary>
    public string? State { get; init; }
    /// <summary>Why, raw.</summary>
    public string? Reason { get; init; }
    /// <summary>The tool that wrote or recalled it.</summary>
    public string? Tool { get; init; }
    /// <summary>The memory store.</summary>
    public string? Store { get; init; }
    /// <summary>The store's scope.</summary>
    public string? Scope { get; init; }
    /// <summary>Where the content came from.</summary>
    public string? Source { get; init; }
    /// <summary><c>gateway</c> / <c>observed</c> — whether the gateway could refuse it.</summary>
    public string? Enforcement { get; init; }
    /// <summary>The gateway-minted reference for this content.</summary>
    public string? MemoryRef { get; init; }
    /// <summary>The store's own id for the write, when given.</summary>
    public string? WriteRef { get; init; }
    /// <summary>Whether this recall was already seen earlier in the session.</summary>
    public bool? SeenBefore { get; init; }
}

/// <summary>
/// The decision's <c>memory</c> block, or its header counts when that is all
/// there is.
/// </summary>
public sealed record MemoryActivity
{
    /// <summary>Memory writes on the request.</summary>
    public int Writes { get; init; }
    /// <summary>Writes the gateway refused to store.</summary>
    public int WritesBlocked { get; init; }
    /// <summary>Memory recalls on the request.</summary>
    public int Recalls { get; init; }
    /// <summary>Recalls the gateway flagged.</summary>
    public int RecallsFlagged { get; init; }
    /// <summary><c>gateway</c> / <c>observed</c> / <c>hosted</c> / <c>mixed</c>, raw; null off a header.</summary>
    public string? Coverage { get; init; }
    /// <summary>Per-item detail. Empty when read off the header.</summary>
    public IReadOnlyList<MemoryItem> Items { get; init; } = Array.Empty<MemoryItem>();
    /// <summary><c>"body"</c> or <c>"header"</c>.</summary>
    public string Carrier { get; init; } = "body";
}

/// <summary>Why a route was refused for the data in the request. Every field is raw.</summary>
public sealed record RoutePolicy
{
    /// <summary>The route that was refused.</summary>
    public string? Route { get; init; }
    /// <summary>The data category it is not approved for, e.g. <c>phi</c>.</summary>
    public string? Category { get; init; }
    /// <summary>Why, raw.</summary>
    public string? Reason { get; init; }
    /// <summary>The route the request asked for, when it differs.</summary>
    public string? RequestedRoute { get; init; }
    /// <summary>The cheaper route a budget limit would have moved it to, when there was one.</summary>
    public string? DegradedRoute { get; init; }
}

/// <summary>
/// Reads the envelope's three optional blocks and the two count-only headers.
/// Tolerant by construction: a block that is not an object is absent, a field
/// of the wrong type is null (or zero, for a count), an unknown field is
/// ignored, and an unknown state is <see cref="AttachmentState.Unknown"/>.
/// </summary>
internal static class DecisionBlocks
{
    internal const string HeaderAttachments = "x-trustgate-attachments";
    internal const string HeaderMemory = "x-trustgate-memory";
    internal const string HeaderInspection = "x-trustgate-inspection";

    internal static Attachments? AttachmentsFromBody(JsonElement block)
    {
        if (block.ValueKind != JsonValueKind.Object)
            return null;
        var items = new List<Attachment>();
        foreach (var item in Objects(block, "items"))
        {
            var findings = new List<AttachmentFinding>();
            foreach (var f in Objects(item, "findings"))
            {
                findings.Add(new AttachmentFinding
                {
                    Detector = Text(f, "detector"),
                    Category = Text(f, "category"),
                    Code = Text(f, "code"),
                    Severity = Text(f, "severity"),
                    Locator = Text(f, "locator"),
                });
            }
            var rawState = Text(item, "state");
            items.Add(new Attachment
            {
                State = StateOf(rawState),
                RawState = rawState,
                Reason = Text(item, "reason"),
                Filename = Text(item, "filename"),
                DeclaredType = Text(item, "declared_type"),
                SniffedType = Text(item, "sniffed_type"),
                Sha256 = Text(item, "sha256"),
                Bytes = item.TryGetProperty("bytes", out var b) && b.ValueKind == JsonValueKind.Number
                    && b.TryGetInt64(out var bytes) ? bytes : null,
                Source = Text(item, "source"),
                Index = item.TryGetProperty("index", out var i) && i.ValueKind == JsonValueKind.Number
                    && i.TryGetInt32(out var index) ? index : null,
                Path = Text(item, "path"),
                Findings = findings.AsReadOnly(),
            });
        }
        return new Attachments
        {
            Total = Count(block, "total"),
            Scanned = Count(block, "scanned"),
            ScannedPartial = Count(block, "scanned_partial"),
            NotInspected = Count(block, "not_inspected"),
            Blocked = Count(block, "blocked"),
            Items = items.AsReadOnly(),
        };
    }

    internal static MemoryActivity? MemoryFromBody(JsonElement block)
    {
        if (block.ValueKind != JsonValueKind.Object)
            return null;
        var items = new List<MemoryItem>();
        foreach (var i in Objects(block, "items"))
        {
            items.Add(new MemoryItem
            {
                Kind = Text(i, "kind"),
                State = Text(i, "state"),
                Reason = Text(i, "reason"),
                Tool = Text(i, "tool"),
                Store = Text(i, "store"),
                Scope = Text(i, "scope"),
                Source = Text(i, "source"),
                Enforcement = Text(i, "enforcement"),
                MemoryRef = Text(i, "memory_ref"),
                WriteRef = Text(i, "write_ref"),
                SeenBefore = i.TryGetProperty("seen_before", out var s)
                    && s.ValueKind is JsonValueKind.True or JsonValueKind.False ? s.GetBoolean() : null,
            });
        }
        return new MemoryActivity
        {
            Writes = Count(block, "writes"),
            WritesBlocked = Count(block, "writes_blocked"),
            Recalls = Count(block, "recalls"),
            RecallsFlagged = Count(block, "recalls_flagged"),
            Coverage = Text(block, "coverage"),
            Items = items.AsReadOnly(),
        };
    }

    internal static RoutePolicy? RoutePolicyFromBody(JsonElement block)
    {
        if (block.ValueKind != JsonValueKind.Object)
            return null;
        return new RoutePolicy
        {
            Route = Text(block, "route"),
            Category = Text(block, "category"),
            Reason = Text(block, "reason"),
            RequestedRoute = Text(block, "requested_route"),
            DegradedRoute = Text(block, "degraded_route"),
        };
    }

    internal static Attachments? AttachmentsFromHeader(string? value)
    {
        var c = Counts(value);
        if (c is null)
            return null;
        var total = c.GetValueOrDefault("total");
        var scanned = c.GetValueOrDefault("scanned");
        var notInspected = c.GetValueOrDefault("not_inspected");
        var blocked = c.GetValueOrDefault("blocked");
        return new Attachments
        {
            Total = total,
            Scanned = scanned,
            // The header does not carry it. The four states partition `total`,
            // so the remainder is scanned_partial — which errs, if the header is
            // ever wrong, toward counting a file as unverified.
            ScannedPartial = Math.Max(0, total - scanned - notInspected - blocked),
            NotInspected = notInspected,
            Blocked = blocked,
            Carrier = "header",
        };
    }

    internal static MemoryActivity? MemoryFromHeader(string? value)
    {
        var c = Counts(value);
        if (c is null)
            return null;
        return new MemoryActivity
        {
            Writes = c.GetValueOrDefault("writes"),
            WritesBlocked = c.GetValueOrDefault("blocked"),
            Recalls = c.GetValueOrDefault("recalls"),
            RecallsFlagged = c.GetValueOrDefault("flagged"),
            Carrier = "header",
        };
    }

    /// <summary>The body's <c>inspection</c> object; text slots and text detector states only.</summary>
    internal static IReadOnlyDictionary<string, object>? InspectionFromBody(JsonElement block)
    {
        if (block.ValueKind != JsonValueKind.Object)
            return null;
        var out_ = new Dictionary<string, object>();
        Dictionary<string, string>? detectors = null;
        foreach (var p in block.EnumerateObject())
        {
            if (p.Name is "input" or "output" or "reason")
            {
                if (p.Value.ValueKind == JsonValueKind.String)
                    out_[p.Name] = p.Value.GetString()!;
            }
            else if (p.Name == "detectors" && p.Value.ValueKind == JsonValueKind.Object)
            {
                foreach (var d in p.Value.EnumerateObject())
                {
                    if (d.Value.ValueKind == JsonValueKind.String)
                        (detectors ??= new Dictionary<string, string>())[d.Name] = d.Value.GetString()!;
                }
            }
        }
        if (detectors is not null)
            out_["detectors"] = new ReadOnlyDictionary<string, string>(detectors);
        return out_.Count == 0 ? null : new ReadOnlyDictionary<string, object>(out_);
    }

    /// <summary>
    /// <c>input=checked;output=skipped;reason=...;memory=partial</c> to the body's shape:
    /// <c>input</c>, <c>output</c> and <c>reason</c> are named slots, every other pair a detector.
    /// </summary>
    internal static IReadOnlyDictionary<string, object>? InspectionFromHeader(string? value)
    {
        if (value is null)
            return null;
        var out_ = new Dictionary<string, object>();
        Dictionary<string, string>? detectors = null;
        foreach (var part in value.Split(';'))
        {
            var eq = part.IndexOf('=');
            if (eq < 0)
                continue;
            var name = part[..eq].Trim();
            var state = part[(eq + 1)..].Trim();
            if (name is "input" or "output" or "reason")
                out_[name] = state;
            else if (name.Length > 0)
                (detectors ??= new Dictionary<string, string>())[name] = state;
        }
        if (detectors is not null)
            out_["detectors"] = new ReadOnlyDictionary<string, string>(detectors);
        return out_.Count == 0 ? null : new ReadOnlyDictionary<string, object>(out_);
    }

    private static AttachmentState StateOf(string? raw) => raw switch
    {
        "scanned" => AttachmentState.Scanned,
        "scanned_partial" => AttachmentState.ScannedPartial,
        "not_inspected" => AttachmentState.NotInspected,
        "blocked" => AttachmentState.Blocked,
        _ => AttachmentState.Unknown,
    };

    /// <summary><c>a=1;b=2</c> to a map; unknown names kept, junk dropped, null when nothing parsed.</summary>
    private static Dictionary<string, int>? Counts(string? value)
    {
        if (value is null)
            return null;
        Dictionary<string, int>? out_ = null;
        foreach (var part in value.Split(';'))
        {
            var eq = part.IndexOf('=');
            if (eq < 0)
                continue;
            var name = part[..eq].Trim();
            var n = part[(eq + 1)..].Trim();
            if (name.Length == 0 || n.Length == 0 || !n.All(char.IsAsciiDigit)
                || !int.TryParse(n, System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var number))
            {
                continue;
            }
            (out_ ??= new Dictionary<string, int>(StringComparer.Ordinal))[name] = number;
        }
        return out_;
    }

    private static IEnumerable<JsonElement> Objects(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array)
            yield break;
        foreach (var el in arr.EnumerateArray())
        {
            if (el.ValueKind == JsonValueKind.Object)
                yield return el;
        }
    }

    private static string? Text(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int Count(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
        && v.TryGetInt32(out var n) && n > 0 ? n : 0;
}
