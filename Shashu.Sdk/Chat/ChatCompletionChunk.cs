using System.Text.Json.Serialization;

namespace Shashu.Chat;

/// <summary>
/// Single delta chunk in a streaming chat-completions response. Same
/// shape OpenAI emits over Server-Sent Events when <c>stream=true</c>.
/// The full content of a streamed response is the concatenation of
/// <c>Choices[0].Delta.Content</c> across all chunks.
/// </summary>
public sealed class ChatCompletionChunk
{
    /// <summary>Provider completion id.</summary>
    [JsonPropertyName("id")]
    public string Id { get; init; } = "";

    /// <summary>OpenAI object name, usually <c>chat.completion.chunk</c>.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Naming", "CA1720", Justification = "OpenAI wire field is named object.")]
    [JsonPropertyName("object")]
    public string Object { get; init; } = "";

    /// <summary>
    /// Unix timestamp. Null on the gateway's allowed streaming path — the
    /// live frame is <c>"created": null</c>, and a non-nullable
    /// <see cref="long"/> throws <see cref="System.Text.Json.JsonException"/>
    /// on every clean reply. Blocks write a real integer.
    /// </summary>
    [JsonPropertyName("created")]
    public long? Created { get; init; }

    /// <summary>Model id on the chunk.</summary>
    [JsonPropertyName("model")]
    public string Model { get; init; } = "";

    /// <summary>Incremental choices in this frame.</summary>
    [JsonPropertyName("choices")]
    public IReadOnlyList<ChatCompletionChunkChoice> Choices { get; init; } = Array.Empty<ChatCompletionChunkChoice>();
}

/// <summary>Single choice's incremental delta within a streaming response.</summary>
public sealed record ChatCompletionChunkChoice(
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("delta")] ChatCompletionDelta Delta,
    [property: JsonPropertyName("finish_reason")] string? FinishReason);

/// <summary>
/// Incremental delta in a single streaming choice. Fields are only
/// present in chunks that include them — e.g. <c>Role</c> typically
/// appears only on the first chunk of a response.
/// </summary>
public sealed record ChatCompletionDelta(
    [property: JsonPropertyName("role")] string? Role,
    [property: JsonPropertyName("content")] string? Content);
