using System.Text.Json;
using System.Text.Json.Serialization;

namespace Shashu.Chat;

/// <summary>
/// Single message in a chat conversation. <c>role</c> is one of
/// <c>system</c> / <c>user</c> / <c>assistant</c> / <c>tool</c>.
/// </summary>
/// <remarks>
/// The optional tool fields (<see cref="ToolCalls"/>, <see cref="ToolCallId"/>,
/// <see cref="Name"/>) serialise at the message level — they are part of the
/// OpenAI message payload that IS forwarded to the LLM provider (the model
/// needs its own prior tool calls), unlike <c>trustgate_metadata</c> which the
/// gateway strips. Emitting them structurally lets the gateway's
/// <c>ingress-tool</c> Shashu scanner read each tool call's function name +
/// arguments directly instead of relying on best-effort de-flattening.
/// All three are omitted from the JSON when unset, so existing
/// <c>new ChatMessage(role, content)</c> callers serialise byte-identically.
/// </remarks>
public sealed record ChatMessage(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] string Content)
{
    /// <summary>
    /// Tool calls the assistant made (assistant messages only). Each entry
    /// mirrors the OpenAI <c>tool_calls</c> shape the gateway parses:
    /// <c>{id, type, function: {name, arguments}}</c>.
    /// </summary>
    [JsonPropertyName("tool_calls")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ToolCall>? ToolCalls { get; init; }

    /// <summary>
    /// Id of the <see cref="ToolCall"/> this message answers
    /// (<c>role: "tool"</c> result messages only).
    /// </summary>
    [JsonPropertyName("tool_call_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ToolCallId { get; init; }

    /// <summary>
    /// Name of the tool that produced this result
    /// (<c>role: "tool"</c> result messages, optional).
    /// </summary>
    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; init; }

    /// <summary>
    /// Build a tool-result message (<c>role: "tool"</c>) answering the tool
    /// call with id <paramref name="toolCallId"/>, so callers don't hand-build
    /// the shape.
    /// </summary>
    public static ChatMessage Tool(string toolCallId, string content, string? name = null) =>
        new("tool", content) { ToolCallId = toolCallId, Name = name };
}

/// <summary>
/// One tool call in an assistant message's <see cref="ChatMessage.ToolCalls"/>.
/// Matches the OpenAI shape the gateway's ingress-tool scanner reads:
/// <c>{id, type, function}</c>.
/// </summary>
public sealed record ToolCall
{
    /// <summary>Provider-assigned tool-call id (echoed back via <see cref="ChatMessage.ToolCallId"/>).</summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>Tool-call kind. Only <c>"function"</c> exists today.</summary>
    [JsonPropertyName("type")]
    public string Type { get; init; } = "function";

    /// <summary>The function invocation payload.</summary>
    [JsonPropertyName("function")]
    public required FunctionCall Function { get; init; }
}

/// <summary>
/// Function name + arguments of a <see cref="ToolCall"/>.
/// </summary>
/// <remarks>
/// <see cref="Arguments"/> is a JSON-<b>encoded string</b> (e.g.
/// <c>"{\"city\":\"Paris\"}"</c>), not a nested object — that is the OpenAI
/// wire shape and exactly what the gateway parses. Do not pre-parse it.
/// </remarks>
public sealed record FunctionCall(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("arguments")] string Arguments);

/// <summary>
/// OpenAI-compatible chat-completions request. Only the fields Shashu
/// inspects/forwards today are typed; pass anything else via the raw
/// <see cref="ExtraFields"/> dictionary if needed (see also the Python
/// SDK's same pattern).
/// </summary>
/// <remarks>
/// Declared as a <c>record</c> so callers + the SDK can use <c>with</c>
/// expressions to build variants (e.g. forcing <c>Stream = true</c> on
/// the streaming code path).
/// </remarks>
public sealed record ChatCompletionRequest
{
    /// <summary>Model id as the gateway knows it (the route name).</summary>
    [JsonPropertyName("model")]
    public required string Model { get; init; }

    /// <summary>Conversation messages, OpenAI chat shape.</summary>
    [JsonPropertyName("messages")]
    public required IReadOnlyList<ChatMessage> Messages { get; init; }

    /// <summary>Sampling temperature, forwarded to the provider.</summary>
    [JsonPropertyName("temperature")]
    public double? Temperature { get; init; }

    /// <summary>Nucleus sampling, forwarded to the provider.</summary>
    [JsonPropertyName("top_p")]
    public double? TopP { get; init; }

    /// <summary>Maximum completion tokens, forwarded to the provider.</summary>
    [JsonPropertyName("max_tokens")]
    public int? MaxTokens { get; init; }

    /// <summary>When true, call <c>CreateStreamAsync</c> instead of <c>CreateAsync</c>.</summary>
    [JsonPropertyName("stream")]
    public bool? Stream { get; init; }

    /// <summary>Stop sequences, forwarded to the provider.</summary>
    [JsonPropertyName("stop")]
    public IReadOnlyList<string>? Stop { get; init; }

    /// <summary>
    /// Optional RAG context. When set, the SDK emits it under
    /// <c>trustgate_metadata.rag_context</c> in the request body so the
    /// gateway routes the retrieved documents through the dedicated
    /// <c>ingress-rag</c> Shashu scanner (which has access to the
    /// corpus + per-doc scores + previews — materially better than
    /// scanning concatenated text on the generic ingress-text surface).
    /// </summary>
    /// <remarks>
    /// The gateway pops <c>trustgate_metadata</c> from the body before
    /// forwarding to the LLM provider, so the upstream model never sees
    /// the control-plane metadata.
    /// </remarks>
    [JsonIgnore]
    public RagContext? RagContext { get; init; }

    /// <summary>
    /// Free-form metadata sent under <c>trustgate_metadata</c> in the
    /// request body. Use for any control-plane fields the gateway adds
    /// after this SDK release without us having to ship typed properties
    /// for them. Mutually composable with <see cref="RagContext"/> — both
    /// land under <c>trustgate_metadata</c>.
    /// </summary>
    [JsonIgnore]
    public IDictionary<string, object?>? ShashuMetadata { get; init; }

    /// <summary>
    /// Computed property that emits the merged <c>trustgate_metadata</c>
    /// blob at serialisation time. Combines <see cref="ShashuMetadata"/>
    /// (free-form) with <see cref="RagContext"/> (typed), preferring an
    /// explicit <c>rag_context</c> entry in <see cref="ShashuMetadata"/>
    /// if the caller chose to set it that way.
    /// </summary>
    /// <remarks>
    /// Hidden from public API surface (protected from caller use) — only
    /// the JSON serialiser invokes it. Returns null when there's nothing
    /// to emit, so the field is omitted from the body.
    /// </remarks>
    [JsonPropertyName("trustgate_metadata")]
    public IDictionary<string, object?>? ShashuMetadataPayload
    {
        get
        {
            if (RagContext is null && (ShashuMetadata is null || ShashuMetadata.Count == 0))
                return null;

            var merged = ShashuMetadata is null
                ? new Dictionary<string, object?>()
                : new Dictionary<string, object?>(ShashuMetadata);
            if (RagContext is not null && !merged.ContainsKey("rag_context"))
                merged["rag_context"] = RagContext;
            return merged;
        }
    }

    /// <summary>
    /// Extension fields not exposed as typed properties. Serialised at the
    /// top level of the JSON request body. Use for provider-specific knobs
    /// (e.g. Anthropic's <c>top_k</c>) that haven't been promoted to a
    /// typed field yet.
    /// </summary>
    [JsonExtensionData]
    public IDictionary<string, object?>? ExtraFields { get; init; }
}

/// <summary>
/// Structured retrieval context for the gateway's <c>ingress-rag</c>
/// Shashu scanner. All fields optional; the gateway falls back gracefully
/// if any are missing.
/// </summary>
/// <remarks>
/// Set <see cref="ChatCompletionRequest.RagContext"/> to attach. The SDK
/// serialises this under <c>trustgate_metadata.rag_context</c>; the
/// gateway strips that key before forwarding to the LLM provider.
/// </remarks>
public sealed record RagContext
{
    /// <summary>Logical name of the corpus the documents were retrieved from.</summary>
    [JsonPropertyName("corpus")]
    public string? Corpus { get; init; }

    /// <summary>The user query that triggered retrieval.</summary>
    [JsonPropertyName("query")]
    public string? Query { get; init; }

    /// <summary>Retrieved documents.</summary>
    [JsonPropertyName("docs")]
    public IReadOnlyList<RagDoc>? Docs { get; init; }
}

/// <summary>
/// One retrieved document in a <see cref="RagContext"/>. The gateway
/// scanner needs at minimum a <see cref="Preview"/> (or
/// <c>content</c> / <c>text</c> alias); <see cref="Id"/> and
/// <see cref="Score"/> are recorded for audit + citation but optional.
/// </summary>
public sealed record RagDoc(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("score")] double? Score,
    [property: JsonPropertyName("preview")] string? Preview);


/// <summary>
/// Single choice (completion) in a chat-completions response.
/// </summary>
public sealed record ChatCompletionChoice(
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("message")] ChatMessage Message,
    [property: JsonPropertyName("finish_reason")] string? FinishReason);

/// <summary>Token-usage breakdown.</summary>
public sealed record ChatCompletionUsage(
    [property: JsonPropertyName("prompt_tokens")] int PromptTokens,
    [property: JsonPropertyName("completion_tokens")] int CompletionTokens,
    [property: JsonPropertyName("total_tokens")] int TotalTokens);

/// <summary>
/// OpenAI-compatible chat-completions response. The Shashu-specific
/// security verdict / audit metadata is attached as <c>x-trustgate-*</c>
/// headers on the response, not in the JSON body — same as the Python /
/// Node SDKs.
/// </summary>
public sealed class ChatCompletionResponse
{
    /// <summary>Provider completion id.</summary>
    [JsonPropertyName("id")]
    public string Id { get; init; } = "";

    /// <summary>OpenAI object name, usually <c>chat.completion</c>.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Naming", "CA1720", Justification = "OpenAI wire field is named object.")]
    [JsonPropertyName("object")]
    public string Object { get; init; } = "";

    /// <summary>
    /// Unix timestamp. Null when the gateway's allowed path emits
    /// <c>"created": null</c> — the same live body that crashed the
    /// streamed half on a non-nullable <see cref="long"/>.
    /// </summary>
    [JsonPropertyName("created")]
    public long? Created { get; init; }

    /// <summary>Model id as returned by the gateway.</summary>
    [JsonPropertyName("model")]
    public string Model { get; init; } = "";

    /// <summary>Completion choices.</summary>
    [JsonPropertyName("choices")]
    public IReadOnlyList<ChatCompletionChoice> Choices { get; init; } = Array.Empty<ChatCompletionChoice>();

    /// <summary>Token usage, when the gateway included it.</summary>
    [JsonPropertyName("usage")]
    public ChatCompletionUsage? Usage { get; init; }

    /// <summary>
    /// Unknown JSON members, including the top-level <c>trustgate</c>
    /// decision envelope. Without this slot the envelope is dropped on
    /// parse and <c>DecisionOf</c> cannot read the body carrier.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtraFields { get; set; }

    /// <summary>
    /// <c>x-trustgate-*</c> (and sibling) response headers captured from
    /// the HTTP response. Empty when the object was not built by this SDK.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyDictionary<string, string> ShashuHeaders { get; set; }
        = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}
