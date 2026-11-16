using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Shashu.Routing;
using Shashu.Internal;
using Shashu.Tests.Harness;
using Xunit;

namespace Shashu.Tests;

/// <summary>
/// The retrieval harness: an app talks to a mock gateway through the real
/// routing handler, and every case asserts on the bytes that arrived.
/// </summary>
/// <remarks>
/// <para>Each case uses a canary nonce, so a payload that arrived from the
/// wrong flow is identifiable rather than merely surprising.</para>
/// <para>No test here opens a socket: the gateway is an
/// <see cref="HttpMessageHandler"/> and the address is a <c>.invalid</c> host
/// that cannot resolve, so a leak fails loudly instead of reaching something
/// real.</para>
/// <para>The absence cases (1, 4, 8, 9) were each reddened by a targeted
/// mutation of the shipping code before being trusted — an inert stub cannot
/// redden a test that asserts nothing is there. The mutation is named in each
/// case's comment.</para>
/// </remarks>
public class RetrievalHarnessTests
{
    // ---------------------------------------------------------------- helpers

    private static (HttpClient Client, MockGateway Gateway) Wire()
    {
        var gateway = new MockGateway();
        var handler = new ShashuRoutingHandler(
            "sk-trustgate-test",
            gateway,
            new ShashuRoutingOptions { BaseUrl = TestGateway.Uri });
        return (new HttpClient(handler), gateway);
    }

    /// <summary>An ordinary chat-completion request aimed at a provider host.</summary>
    /// <remarks>
    /// Deliberately INDENTED. A compact body would survive a needless
    /// parse-and-re-serialise byte-for-byte, so the zero-cost case could pass
    /// against a joiner that rewrote every request. Indentation makes any
    /// rewrite at all visible in the bytes.
    /// </remarks>
    private static HttpRequestMessage Completion(string nonce = "prompt") =>
        new(HttpMethod.Post, "https://api.openai.com/v1/chat/completions")
        {
            Content = new StringContent(
                new JsonObject
                {
                    ["model"] = "gpt-4o-mini",
                    ["messages"] = new JsonArray(
                        new JsonObject { ["role"] = "user", ["content"] = nonce }),
                }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
                Encoding.UTF8,
                "application/json"),
        };

    private static async Task<RecordedRequest> SendOne(string nonce = "prompt")
    {
        var (client, gateway) = Wire();
        using (client)
        {
            using var response = await client.SendAsync(Completion(nonce));
            return gateway.Single();
        }
    }

    // ------------------------------------------------------- 1. NEGATIVE

    [Fact]
    public async Task Case01_No_declaration_means_no_trustgate_metadata_at_all()
    {
        // RED-FIRST: reddened by mutating RetrievalJoiner.JoinAsync's guard from
        // `if (!RetrievalBus.HasEvents()) return;` to always continue and attach
        // an empty-docs context ("always-attach"). With that mutation the
        // request carries trustgate_metadata and this fails.
        RetrievalBus.Clear();

        var recorded = await SendOne("no retrieval here");

        Assert.False(recorded.HasShashuMetadata);
        Assert.Null(recorded.RagContext);
        Assert.DoesNotContain("trustgate_metadata", Encoding.UTF8.GetString(recorded.Body));
    }

    // ------------------------------------------------- 2. the happy path

    [Fact]
    public async Task Case02_A_declaration_arrives_schema_valid_and_carries_the_nonce()
    {
        const string nonce = "canary-02-a4f1";
        RetrievalBus.Clear();

        Retrieval.DeclareRetrieval(
            new[] { new { Id = "POL-1", Preview = $"policy text {nonce}", Score = 0.92 } },
            corpus: "policy_docs_v2",
            query: "refinance eligibility");

        var recorded = await SendOne();

        var rag = recorded.RagContext;
        Assert.NotNull(rag);

        var errors = RagContextSchema.Validate(rag);
        Assert.True(errors.Count == 0, string.Join("; ", errors));

        Assert.Equal("policy_docs_v2", rag!["corpus"]!.GetValue<string>());
        Assert.Equal("refinance eligibility", rag["query"]!.GetValue<string>());
        Assert.Equal("manual", rag["source"]!.GetValue<string>());
        Assert.Equal(1, rag["v"]!.GetValue<int>());

        var docs = rag["docs"]!.AsArray();
        Assert.Single(docs);
        Assert.Equal("POL-1", docs[0]!["id"]!.GetValue<string>());
        Assert.Contains(nonce, docs[0]!["preview"]!.GetValue<string>());
        Assert.Equal(0.92, docs[0]!["score"]!.GetValue<double>(), 6);

        // The customer's own body is untouched beside it.
        Assert.Equal("gpt-4o-mini", recorded.Json!["model"]!.GetValue<string>());
    }

    // ------------------------------------------- 3. explicit always wins

    [Fact]
    public async Task Case03_An_explicit_rag_context_arrives_unmodified_over_a_populated_bus()
    {
        const string explicitNonce = "canary-03-explicit";
        const string ambientNonce = "canary-03-ambient";
        RetrievalBus.Clear();

        Retrieval.DeclareRetrieval(
            new[] { $"ambient {ambientNonce}" }, corpus: "ambient_corpus");

        var body = new JsonObject
        {
            ["model"] = "gpt-4o-mini",
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "user", ["content"] = "q" }),
            ["trustgate_metadata"] = new JsonObject
            {
                ["rag_context"] = new JsonObject
                {
                    ["corpus"] = "explicit_corpus",
                    ["docs"] = new JsonArray(new JsonObject
                    {
                        ["id"] = "EXP-1",
                        ["preview"] = explicitNonce,
                    }),
                },
            },
        };

        var (client, gateway) = Wire();
        using (client)
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post, "https://api.openai.com/v1/chat/completions")
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            using var response = await client.SendAsync(request);
        }

        var rag = gateway.Single().RagContext!;
        Assert.Equal("explicit_corpus", rag["corpus"]!.GetValue<string>());
        Assert.Single(rag["docs"]!.AsArray());
        Assert.Equal(explicitNonce, rag["docs"]![0]!["preview"]!.GetValue<string>());
        Assert.DoesNotContain(ambientNonce, Encoding.UTF8.GetString(gateway.Single().Body));

        // The ambient declaration was consumed, not deferred: it must not turn
        // up on the next call.
        Assert.False(RetrievalBus.HasEvents());
    }

    // ------------------------------------------- 4. snapshot and clear

    [Fact]
    public async Task Case04_Declaring_once_then_sending_twice_carries_it_only_on_the_first()
    {
        // RED-FIRST: reddened by mutating RetrievalJoiner to call
        // RetrievalBus.Snapshot() instead of TakeAll() ("declare-sends", i.e.
        // read without consuming). The second request then carries it too.
        const string nonce = "canary-04-9c2b";
        RetrievalBus.Clear();

        Retrieval.DeclareRetrieval(new[] { $"doc {nonce}" }, corpus: "corpus_once");

        var (client, gateway) = Wire();
        using (client)
        {
            using (var first = await client.SendAsync(Completion("first"))) { }
            using (var second = await client.SendAsync(Completion("second"))) { }
        }

        var requests = gateway.Requests;
        Assert.Equal(2, requests.Count);

        Assert.NotNull(requests[0].RagContext);
        Assert.Contains(nonce, Encoding.UTF8.GetString(requests[0].Body));

        Assert.Null(requests[1].RagContext);
        Assert.False(requests[1].HasShashuMetadata);
        Assert.DoesNotContain(nonce, Encoding.UTF8.GetString(requests[1].Body));
    }

    // ---------------------------------------------- 5. concurrency

    [Fact]
    public async Task Case05_Eight_interleaved_tasks_do_not_contaminate_each_other()
    {
        var results = new ConcurrentBag<(int Task, string Corpus, string Preview, int DocCount)>();

        await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(async () =>
        {
            // Each task declares its own nonce and sends its own request,
            // interleaved so the flows genuinely overlap.
            await Task.Delay(1 + (i % 3));
            Retrieval.DeclareRetrieval(
                new[] { $"canary-05-task-{i}" }, corpus: $"corpus-{i}", query: $"q-{i}");
            await Task.Delay(1 + ((i + 1) % 3));

            var recorded = await SendOne($"prompt-{i}");
            var rag = recorded.RagContext!;
            var docs = rag["docs"]!.AsArray();
            results.Add((i, rag["corpus"]!.GetValue<string>(),
                docs[0]!["preview"]!.GetValue<string>(), docs.Count));
        })));

        Assert.Equal(8, results.Count);
        foreach (var (task, corpus, preview, docCount) in results)
        {
            Assert.Equal($"corpus-{task}", corpus);
            Assert.Equal($"canary-05-task-{task}", preview);
            Assert.Equal(1, docCount);
        }
    }

    // ------------------------- 6. the AsyncLocal child-scope case, asserted

    [Fact]
    public async Task Case06_A_declaration_in_an_awaited_helper_needs_a_scope_to_arrive()
    {
        const string nonce = "canary-06-deep";

        static async Task Retrieve()
        {
            await Task.Yield();   // the async boundary a rebind cannot climb back over
            Retrieval.DeclareRetrieval(new[] { $"deep {nonce}" }, corpus: "deep_corpus");
        }

        // Without a scope: the declaration does NOT arrive. Asserted, not
        // wished away — this is the measured boundary from the probe.
        RetrievalBus.Clear();
        await Retrieve();
        var without = await SendOne();
        Assert.Null(without.RagContext);
        Assert.DoesNotContain(nonce, Encoding.UTF8.GetString(without.Body));

        // With a scope around the same call: it does.
        RetrievalBus.Clear();
        RecordedRequest with;
        using (Retrieval.RetrievalScope(new[] { "top-level doc" }, corpus: "scope_corpus"))
        {
            await Retrieve();
            with = await SendOne();
        }

        Assert.NotNull(with.RagContext);
        Assert.Contains(nonce, Encoding.UTF8.GetString(with.Body));
        // Two corpora were folded, so the contract's single corpus field names both.
        Assert.Equal("scope_corpus+deep_corpus", with.RagContext!["corpus"]!.GetValue<string>());
        Assert.True(RagContextSchema.IsValid(with.RagContext));
    }

    // ------------------------------------------------------ 7. oversize

    [Fact]
    public async Task Case07_Oversize_input_is_truncated_to_the_caps_and_stays_schema_valid()
    {
        RetrievalBus.Clear();

        // 100 documents (cap 64), each with a 5000-character preview (cap 2048).
        var docs = Enumerable.Range(0, 100)
            .Select(i => new { Id = $"DOC-{i}", Preview = new string('x', 5000) + $"-{i}" })
            .ToArray();

        Retrieval.DeclareRetrieval(docs, corpus: new string('c', 400), query: new string('q', 9000));

        var recorded = await SendOne();
        var rag = recorded.RagContext!;

        var errors = RagContextSchema.Validate(rag);
        Assert.True(errors.Count == 0, string.Join("; ", errors));

        var wire = rag["docs"]!.AsArray();
        Assert.Equal(SdkSurface.MaxDocs, wire.Count);
        Assert.All(wire, d =>
            Assert.True(d!["preview"]!.GetValue<string>().Length <= SdkSurface.MaxPreviewChars));

        Assert.Equal(SdkSurface.MaxCorpusChars, rag["corpus"]!.GetValue<string>().Length);
        Assert.Equal(SdkSurface.MaxQueryChars, rag["query"]!.GetValue<string>().Length);

        // Trimming keeps the NEWEST: the declaration nearest the model call is
        // the one describing the prompt, so the tail survives, not the head.
        Assert.Equal("DOC-36", wire[0]!["id"]!.GetValue<string>());
        Assert.Equal("DOC-99", wire[^1]!["id"]!.GetValue<string>());
    }

    // ----------------------------------------------------- 8. zero cost

    [Fact]
    public async Task Case08_An_empty_bus_sends_bytes_identical_to_the_unpatched_handler()
    {
        // RED-FIRST: reddened by the same "always-attach" mutation as case 1 —
        // any body rewrite at all, even one that re-serialises to equivalent
        // JSON, changes the bytes and fails this.
        RetrievalBus.Clear();

        // The unpatched baseline: the same request straight at the recorder.
        var bare = new MockGateway();
        using (var plain = new HttpClient(bare))
        {
            using var response = await plain.SendAsync(
                new HttpRequestMessage(HttpMethod.Post, "https://gateway.test.invalid/v1/chat/completions")
                {
                    Content = Completion("zero-cost").Content,
                });
        }

        RetrievalBus.Clear();
        var patched = await SendOne("zero-cost");

        Assert.Equal(bare.Single().Body, patched.Body);
        Assert.Equal(bare.Single().ContentLength, patched.ContentLength);
        Assert.Equal(bare.Single().ContentType, patched.ContentType);
    }

    // -------------------------------------- 10. (9 lives in EnvTests)

    [Fact]
    public async Task Case11_Content_length_matches_the_rewritten_body()
    {
        // The joiner replaces the content; a stale Content-Length would make the
        // gateway read a truncated body — silently, on exactly the requests that
        // carry retrieval.
        RetrievalBus.Clear();
        Retrieval.DeclareRetrieval(new[] { "a document long enough to change the length" }, "corpus");

        var recorded = await SendOne();

        Assert.NotNull(recorded.RagContext);
        Assert.Equal(recorded.Body.LongLength, recorded.ContentLength);
        Assert.Equal("application/json; charset=utf-8", recorded.ContentType);
    }

    [Fact]
    public async Task A_non_provider_host_is_passed_through_and_keeps_the_declaration()
    {
        // A declaration is for the MODEL call. An unrelated request must not
        // carry a customer's document previews to a third party, and must not
        // consume the declaration either.
        RetrievalBus.Clear();
        Retrieval.DeclareRetrieval(new[] { "canary-passthrough" }, "corpus");

        var (client, gateway) = Wire();
        using (client)
        {
            using var response = await client.SendAsync(
                new HttpRequestMessage(HttpMethod.Post, "https://example.test.invalid/webhook")
                {
                    Content = new StringContent("{\"a\":1}", Encoding.UTF8, "application/json"),
                });
        }

        Assert.Null(gateway.Single().RagContext);
        Assert.DoesNotContain("canary-passthrough", Encoding.UTF8.GetString(gateway.Single().Body));
        Assert.True(RetrievalBus.HasEvents());
        RetrievalBus.Clear();
    }

    [Fact]
    public async Task A_non_json_body_is_passed_through_and_keeps_the_declaration()
    {
        RetrievalBus.Clear();
        Retrieval.DeclareRetrieval(new[] { "canary-nonjson" }, "corpus");

        var (client, gateway) = Wire();
        using (client)
        {
            using var response = await client.SendAsync(
                new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/audio/transcriptions")
                {
                    Content = new ByteArrayContent([0x00, 0x01, 0x02])
                    {
                        Headers = { ContentType = new("application/octet-stream") },
                    },
                });
        }

        Assert.Equal([0x00, 0x01, 0x02], gateway.Single().Body);
        Assert.True(RetrievalBus.HasEvents());
        RetrievalBus.Clear();
    }
}
