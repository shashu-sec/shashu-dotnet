using System.Collections;
using System.Collections.Immutable;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;

namespace Shashu.Internal;

/// <summary>
/// Turns whatever a retriever handed the customer into <c>rag_context.v1</c>,
/// and folds the carried declarations into the one block the joiner attaches.
/// </summary>
/// <remarks>
/// Sibling of the Python SDK's <c>trustgate/rag.py</c> and the Node SDK's
/// <c>src/rag.ts</c>. The reading rules are a DATA table
/// (<see cref="FieldKeys"/>) rather than a chain of branches, so a document
/// type that spells its fields the usual way needs no new code at all — and
/// <c>RagCoercionTests</c> holds a case for every row.
/// </remarks>
internal static class RagContextBuilder
{
    /// <summary>
    /// <c>rag_context.v1</c> <c>source.enum</c>. Restated here rather than read
    /// from the schema at load time: the SDK must not need a JSON file on disk
    /// to make a call. <c>RagContextSchemaTests</c> holds this set to the
    /// schema's enum, so the two cannot drift apart unnoticed.
    /// </summary>
    internal static readonly ImmutableArray<string> Sources = ImmutableArray.Create(
        "manual", "langchain", "llamaindex", "tool", "otel", "n8n", "provider", "other");

    /// <summary><c>docs.items.properties.hash.pattern</c>.</summary>
    private static readonly Regex Hex = new("^[0-9a-fA-F]{8,64}$", RegexOptions.Compiled);

    /// <summary><c>docs.items.properties.score</c> bounds.</summary>
    private const double ScoreMin = -1.0;
    private const double ScoreMax = 1000000.0;

    /// <summary><c>docs.items.properties.id.maxLength</c>.</summary>
    private const int MaxIdChars = 512;

    /// <summary>
    /// <c>source_uri</c> has no maximum in the contract; this one is ours, so a
    /// retriever that puts a whole document in a URI field cannot bloat the
    /// body.
    /// </summary>
    private const int MaxSourceUriChars = 2048;

    // --- The coercion table -------------------------------------------------
    //
    // Field names come from these key lists, tried in order. A .NET retriever
    // hands back POCOs, records, dictionaries or plain strings; all four are
    // read through the same table by property name or dictionary key.

    private static readonly string[] PreviewKeys = ["preview", "text", "content", "page_content", "pageContent", "body"];
    private static readonly string[] IdKeys = ["id", "doc_id", "docId", "document_id", "documentId", "node_id", "nodeId", "_id"];
    private static readonly string[] ScoreKeys = ["score", "similarity", "relevance"];
    private static readonly string[] SourceUriKeys = ["source_uri", "sourceUri", "url", "uri", "source", "file_path", "filePath", "file_name", "fileName"];
    private static readonly string[] HashKeys = ["hash", "sha256", "digest"];

    /// <summary>The five fields, and the names a retriever might spell each with.</summary>
    internal static readonly ImmutableArray<(string Field, string[] Keys)> FieldKeys =
        ImmutableArray.Create(
            ("preview", PreviewKeys),
            ("id", IdKeys),
            ("score", ScoreKeys),
            ("source_uri", SourceUriKeys),
            ("hash", HashKeys));

    /// <summary>
    /// Coerce a caller's documents into the wire shape, dropping any the table
    /// cannot read text out of.
    /// </summary>
    /// <remarks>
    /// A document with no readable text is dropped rather than sent:
    /// <c>preview</c> has <c>minLength: 1</c>, and text the SDK cannot read is
    /// text the gateway cannot scan.
    /// </remarks>
    internal static ImmutableList<RagContextDoc> CoerceDocs(object? docs)
    {
        var builder = ImmutableList.CreateBuilder<RagContextDoc>();
        var position = 0;
        foreach (var doc in AsDocumentList(docs))
        {
            position++;
            var normalised = Normalise(Read(doc), position);
            if (normalised is not null) builder.Add(normalised);
        }
        return builder.ToImmutable();
    }

    /// <summary>
    /// Treat a single document as a one-element list.
    /// </summary>
    /// <remarks>
    /// A bare string is <see cref="IEnumerable"/>, and iterating it would
    /// declare one document per CHARACTER — a wall of single-letter previews
    /// instead of one document.
    /// </remarks>
    private static IEnumerable<object?> AsDocumentList(object? docs)
    {
        switch (docs)
        {
            case null:
                yield break;
            case string:
                yield return docs;
                yield break;
            case IEnumerable enumerable:
                foreach (var item in enumerable) yield return item;
                yield break;
            default:
                yield return docs;
                yield break;
        }
    }

    /// <summary>Read the five fields out of one document, by whatever it is.</summary>
    private static Dictionary<string, object?> Read(object? doc)
    {
        var found = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (doc is null) return found;
        if (doc is string text)
        {
            found["preview"] = text;
            return found;
        }

        var lookup = LookupFor(doc);
        foreach (var (field, keys) in FieldKeys)
        {
            foreach (var key in keys)
            {
                var value = lookup(key);
                if (value is not null) { found[field] = value; break; }
            }
        }

        // Gaps filled from a `metadata` bag, the way LangChain and LlamaIndex
        // ports spell provenance. Never the text: a retriever that put the body
        // in metadata would double it into the payload.
        if (lookup("metadata") is { } metadata && metadata is not string)
        {
            var meta = LookupFor(metadata);
            foreach (var (field, keys) in FieldKeys)
            {
                if (field == "preview" || found.ContainsKey(field)) continue;
                foreach (var key in keys)
                {
                    var value = meta(key);
                    if (value is not null) { found[field] = value; break; }
                }
            }
        }

        return found;
    }

    /// <summary>
    /// A key reader for one object: dictionary lookup where it is a dictionary,
    /// otherwise a case-insensitive public property or field.
    /// </summary>
    private static Func<string, object?> LookupFor(object doc)
    {
        if (doc is IDictionary dictionary)
        {
            return key =>
            {
                foreach (DictionaryEntry entry in dictionary)
                {
                    if (entry.Key is string k && string.Equals(k, key, StringComparison.OrdinalIgnoreCase))
                        return entry.Value;
                }
                return null;
            };
        }

        var type = doc.GetType();
        return key =>
        {
            var property = type.GetProperty(
                key, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (property is not null && property.GetIndexParameters().Length == 0)
                return SafeGet(() => property.GetValue(doc));

            var field = type.GetField(
                key, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            return field is not null ? SafeGet(() => field.GetValue(doc)) : null;
        };
    }

    /// <summary>
    /// A customer's property getter is customer code and may throw. A retrieval
    /// declaration must never be the thing that breaks their request, so a
    /// throwing member reads as absent.
    /// </summary>
    private static object? SafeGet(Func<object?> get)
    {
        try { return get(); }
        catch (TargetInvocationException) { return null; }
        catch (MethodAccessException) { return null; }
        catch (NotSupportedException) { return null; }
    }

    private static RagContextDoc? Normalise(Dictionary<string, object?> raw, int position)
    {
        var preview = AsText(Value(raw, "preview"), RetrievalBus.MaxPreviewChars);
        if (preview is null) return null;

        return new RagContextDoc(
            Id: AsId(Value(raw, "id"), position),
            Preview: preview,
            Score: AsScore(Value(raw, "score")),
            SourceUri: AsText(Value(raw, "source_uri"), MaxSourceUriChars),
            Hash: AsHash(Value(raw, "hash")));
    }

    private static object? Value(Dictionary<string, object?> raw, string key) =>
        raw.TryGetValue(key, out var value) ? value : null;

    private static string? AsText(object? value, int limit)
    {
        if (value is not string text) return null;
        if (text.Length > limit) text = text[..limit];
        return text.Length == 0 ? null : text;
    }

    /// <summary>A stable id, else a positional one. Never derived from document text.</summary>
    private static string AsId(object? value, int position)
    {
        if (value is string text)
        {
            var cleaned = text.Trim();
            if (cleaned.Length > MaxIdChars) cleaned = cleaned[..MaxIdChars];
            if (cleaned.Length > 0) return cleaned;
        }
        if (value is int or long or short or uint or ulong or ushort)
            return Convert.ToString(value, CultureInfo.InvariantCulture)!;
        if (value is Guid guid) return guid.ToString();
        return $"doc-{position}";
    }

    private static double? AsScore(object? value)
    {
        double score;
        switch (value)
        {
            case bool:
                return null;
            case string text when double.TryParse(
                text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed):
                score = parsed;
                break;
            case string:
                return null;
            case IConvertible convertible and (double or float or decimal or int or long or short or uint or ulong or ushort or byte or sbyte):
                score = convertible.ToDouble(CultureInfo.InvariantCulture);
                break;
            default:
                return null;
        }

        // JSON has no NaN or Infinity literal, so an unrepresentable score is
        // dropped rather than sent — and dropping the score keeps the document,
        // which still has text worth scanning.
        if (double.IsNaN(score) || double.IsInfinity(score)) return null;
        return score is < ScoreMin or > ScoreMax ? null : score;
    }

    private static string? AsHash(object? value) =>
        value is string text && Hex.IsMatch(text) ? text : null;

    /// <summary>
    /// Fold carried declarations into one <c>rag_context.v1</c> block, or null
    /// when nothing survived.
    /// </summary>
    /// <remarks>
    /// <para>The contract carries a single <c>corpus</c>, <c>query</c> and
    /// <c>source</c> for the whole declaration, so multiple events have to be
    /// reconciled:</para>
    /// <list type="bullet">
    ///   <item><description><c>corpus</c> — the one they share, or the distinct
    ///   ids joined with <c>+</c>. A hybrid retrieval across two indexes is a
    ///   real shape, and naming only one of them would misattribute the other's
    ///   documents.</description></item>
    ///   <item><description><c>source</c> — the one they share, else
    ///   <c>other</c>, the enum value that exists for exactly this.</description></item>
    ///   <item><description><c>query</c> — the most recent non-empty one: the
    ///   query nearest the call.</description></item>
    /// </list>
    /// <para>Documents are de-duplicated by <c>(id, preview)</c>, first
    /// occurrence kept — the pair, not the id alone, because a positional id
    /// (<c>doc-1</c>) is assigned per event, so two unrelated retrievals both
    /// produce a <c>doc-1</c> and matching on the id would silently discard the
    /// second one's first document.</para>
    /// <para>Returns null when nothing survived, so the joiner never writes an
    /// empty <c>docs</c> array (<c>minItems: 1</c>).</para>
    /// </remarks>
    internal static Dictionary<string, object?>? Build(ImmutableList<RetrievalEvent> events)
    {
        if (events.IsEmpty) return null;

        var seen = new HashSet<(string, string)>();
        var docs = new List<RagContextDoc>();
        foreach (var evt in events)
        {
            foreach (var doc in evt.Docs)
            {
                if (seen.Add((doc.Id, doc.Preview))) docs.Add(doc);
            }
        }
        if (docs.Count == 0) return null;
        if (docs.Count > RetrievalBus.MaxDocs)
            docs = docs.GetRange(docs.Count - RetrievalBus.MaxDocs, RetrievalBus.MaxDocs);

        var corpora = new List<string>();
        foreach (var evt in events)
        {
            if (!corpora.Contains(evt.Corpus, StringComparer.Ordinal)) corpora.Add(evt.Corpus);
        }
        var corpus = corpora.Count == 1 ? corpora[0] : string.Join("+", corpora);
        if (corpus.Length > RetrievalBus.MaxCorpusChars)
            corpus = corpus[..RetrievalBus.MaxCorpusChars].TrimEnd('+');

        var sources = new HashSet<string>(events.Select(e => e.Source), StringComparer.Ordinal);
        var query = events.LastOrDefault(e => !string.IsNullOrEmpty(e.Query))?.Query;

        var context = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["v"] = 1,
            ["corpus"] = corpus,
            ["source"] = sources.Count == 1 ? sources.First() : "other",
            ["docs"] = docs.Select(ToWire).ToList(),
        };
        if (query is not null) context["query"] = query;
        return context;
    }

    /// <summary>
    /// One document as the JSON object the gateway reads. Optional keys are
    /// omitted rather than sent null: the schema is
    /// <c>additionalProperties: false</c> with typed optionals, and a null
    /// would fail validation on a field the caller simply did not have.
    /// </summary>
    private static Dictionary<string, object?> ToWire(RagContextDoc doc)
    {
        var wire = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = doc.Id,
            ["preview"] = doc.Preview,
        };
        if (doc.Score is { } score) wire["score"] = score;
        if (doc.SourceUri is { } uri) wire["source_uri"] = uri;
        if (doc.Hash is { } hash) wire["hash"] = hash;
        return wire;
    }
}
