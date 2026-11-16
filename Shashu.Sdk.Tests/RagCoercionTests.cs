using Shashu.Internal;
using Xunit;

namespace Shashu.Tests;

/// <summary>
/// The coercion table: what a .NET retriever can hand back and still be read.
/// </summary>
/// <remarks>
/// The rules are a data table in <see cref="RagContextBuilder"/> rather than a
/// chain of branches, so these cases are the table's rows. A document shape
/// that spells its fields the usual way needs no new SDK code — only a row
/// here proving it.
/// </remarks>
public class RagCoercionTests
{
    private sealed record LangChainish(string PageContent, Dictionary<string, object?> Metadata);
    private sealed record ScoredHit(string Id, string Text, double Score, string Url);
    private sealed class Thrower
    {
        public string Preview => throw new InvalidOperationException("customer getter blew up");
    }

    [Fact]
    public void A_bare_string_is_one_document_not_one_per_character()
    {
        var docs = RagContextBuilder.CoerceDocs("a single document");

        Assert.Single(docs);
        Assert.Equal("a single document", docs[0].Preview);
        Assert.Equal("doc-1", docs[0].Id);
    }

    [Fact]
    public void A_dictionary_is_read_by_key()
    {
        var docs = RagContextBuilder.CoerceDocs(new[]
        {
            new Dictionary<string, object?>
            {
                ["id"] = "D-1", ["preview"] = "text", ["score"] = 0.5,
                ["source_uri"] = "s3://bucket/k", ["hash"] = "deadbeef",
            },
        });

        var doc = Assert.Single(docs);
        Assert.Equal("D-1", doc.Id);
        Assert.Equal("text", doc.Preview);
        Assert.Equal(0.5, doc.Score);
        Assert.Equal("s3://bucket/k", doc.SourceUri);
        Assert.Equal("deadbeef", doc.Hash);
    }

    [Fact]
    public void A_POCO_is_read_by_property_including_the_alias_names()
    {
        var docs = RagContextBuilder.CoerceDocs(new[] { new ScoredHit("H-1", "hit text", 12.5, "https://x/y") });

        var doc = Assert.Single(docs);
        Assert.Equal("H-1", doc.Id);
        Assert.Equal("hit text", doc.Preview);   // read from `Text`
        Assert.Equal(12.5, doc.Score);
        Assert.Equal("https://x/y", doc.SourceUri);   // read from `Url`
    }

    [Fact]
    public void A_metadata_bag_fills_gaps_but_never_supplies_the_text()
    {
        var docs = RagContextBuilder.CoerceDocs(new[]
        {
            new LangChainish("page body", new Dictionary<string, object?>
            {
                ["id"] = "M-1",
                ["source"] = "file:///doc.pdf",
                // A retriever that put the body in metadata must not double it.
                ["text"] = "SHOULD NOT BE USED",
            }),
        });

        var doc = Assert.Single(docs);
        Assert.Equal("page body", doc.Preview);
        Assert.Equal("M-1", doc.Id);
        Assert.Equal("file:///doc.pdf", doc.SourceUri);
    }

    [Fact]
    public void A_document_with_no_readable_text_is_dropped_not_stringified()
    {
        // preview has minLength 1, and text the SDK cannot read is text the
        // gateway cannot scan.
        var docs = RagContextBuilder.CoerceDocs(new object?[] { new { Colour = "blue" }, null, 42 });

        Assert.Empty(docs);
    }

    [Fact]
    public void A_throwing_property_reads_as_absent_rather_than_breaking_the_request()
    {
        var docs = RagContextBuilder.CoerceDocs(new object[] { new Thrower() });

        Assert.Empty(docs);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-2.0)]           // below the contract's minimum of -1
    [InlineData(2_000_000.0)]    // above the contract's maximum of 1e6
    public void An_unrepresentable_score_is_dropped_and_the_document_kept(double score)
    {
        var docs = RagContextBuilder.CoerceDocs(new[]
        {
            new Dictionary<string, object?> { ["preview"] = "text", ["score"] = score },
        });

        var doc = Assert.Single(docs);
        Assert.Null(doc.Score);
        Assert.Equal("text", doc.Preview);
    }

    [Fact]
    public void A_malformed_hash_is_dropped_rather_than_failing_the_whole_payload()
    {
        // additionalProperties:false plus a pattern means one bad hash would
        // reject the entire block, so the field goes and the document stays.
        var docs = RagContextBuilder.CoerceDocs(new[]
        {
            new Dictionary<string, object?> { ["preview"] = "t", ["hash"] = "not-hex!" },
        });

        Assert.Null(Assert.Single(docs).Hash);
    }

    [Fact]
    public void A_blank_corpus_throws_at_the_call_site()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            Retrieval.DeclareRetrieval(new[] { "d" }, corpus: "   "));
        Assert.Equal("corpus", ex.ParamName);
    }

    [Fact]
    public void A_source_outside_the_contract_enum_throws_at_the_call_site()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            Retrieval.DeclareRetrieval(new[] { "d" }, corpus: "c", source: "pinecone"));
        Assert.Equal("source", ex.ParamName);
        Assert.Contains("rag_context.v1", ex.Message);
    }

    [Theory]
    [InlineData("manual")]
    [InlineData("langchain")]
    [InlineData("llamaindex")]
    [InlineData("tool")]
    [InlineData("otel")]
    [InlineData("n8n")]
    [InlineData("provider")]
    [InlineData("other")]
    public void Every_contract_source_value_is_accepted(string source)
    {
        RetrievalBus.Clear();
        Retrieval.DeclareRetrieval(new[] { "d" }, corpus: "c", source: source);
        Assert.Equal(source, RetrievalBus.Snapshot()[0].Source);
        RetrievalBus.Clear();
    }

    [Fact]
    public void Duplicate_documents_are_deduped_by_id_and_preview_first_kept()
    {
        RetrievalBus.Clear();
        Retrieval.DeclareRetrieval(
            new[]
            {
                new Dictionary<string, object?> { ["id"] = "A", ["preview"] = "one" },
                new Dictionary<string, object?> { ["id"] = "A", ["preview"] = "one" },
                new Dictionary<string, object?> { ["id"] = "A", ["preview"] = "two" },
            },
            corpus: "c");

        var built = RagContextBuilder.Build(RetrievalBus.Snapshot())!;
        var docs = (List<Dictionary<string, object?>>)built["docs"]!;

        // The PAIR, not the id alone: the third document shares an id but is a
        // different document and must survive.
        Assert.Equal(2, docs.Count);
        Assert.Equal("one", docs[0]["preview"]);
        Assert.Equal("two", docs[1]["preview"]);
        RetrievalBus.Clear();
    }

    [Fact]
    public void Two_corpora_are_both_named_rather_than_one_being_misattributed()
    {
        RetrievalBus.Clear();
        Retrieval.DeclareRetrieval(new[] { "a" }, corpus: "index-a", source: "manual");
        Retrieval.DeclareRetrieval(new[] { "b" }, corpus: "index-b", source: "langchain");

        var built = RagContextBuilder.Build(RetrievalBus.Snapshot())!;

        Assert.Equal("index-a+index-b", built["corpus"]);
        Assert.Equal("other", built["source"]);   // they disagree, so: other
        RetrievalBus.Clear();
    }

    [Fact]
    public void The_query_nearest_the_call_is_the_one_carried()
    {
        RetrievalBus.Clear();
        Retrieval.DeclareRetrieval(new[] { "a" }, corpus: "c", query: "first");
        Retrieval.DeclareRetrieval(new[] { "b" }, corpus: "c", query: "second");

        Assert.Equal("second", RagContextBuilder.Build(RetrievalBus.Snapshot())!["query"]);
        RetrievalBus.Clear();
    }

    [Fact]
    public void More_than_MaxEvents_declarations_keep_the_newest()
    {
        RetrievalBus.Clear();
        for (var i = 0; i < SdkSurface.MaxEvents + 4; i++)
            Retrieval.DeclareRetrieval(new[] { $"doc-{i}" }, corpus: $"c-{i}");

        var events = RetrievalBus.Snapshot();

        Assert.Equal(SdkSurface.MaxEvents, events.Count);
        Assert.Equal("c-4", events[0].Corpus);
        Assert.Equal($"c-{SdkSurface.MaxEvents + 3}", events[^1].Corpus);
        RetrievalBus.Clear();
    }

    [Fact]
    public void A_declaration_with_no_readable_documents_is_not_carried_at_all()
    {
        // docs has minItems 1, so an empty array would be an invalid payload.
        RetrievalBus.Clear();
        Retrieval.DeclareRetrieval(new object[] { new { Colour = "blue" } }, corpus: "c");

        Assert.False(RetrievalBus.HasEvents());
    }
}
