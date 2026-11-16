namespace Shashu.Chat;

/// <summary>
/// Resource namespace for chat-completions. Mirrors the Python SDK's
/// <c>client.chat.completions</c> path so cross-language migration is
/// mechanical.
/// </summary>
public sealed class ChatResource
{
    /// <summary>Chat completions resource (<c>POST /v1/chat/completions</c>).</summary>
    public ChatCompletionsResource Completions { get; }

    internal ChatResource(ShashuApi api)
    {
        Completions = new ChatCompletionsResource(api);
    }
}

/// <summary>
/// Chat completions endpoint. <c>POST /v1/chat/completions</c>.
/// </summary>
public sealed class ChatCompletionsResource
{
    private readonly ShashuApi _api;

    internal ChatCompletionsResource(ShashuApi api) => _api = api;

    /// <summary>
    /// Send a non-streaming chat completion request through the Shashu
    /// security pipeline. Returns the OpenAI-compatible response shape —
    /// drop-in replacement for <c>OpenAIClient.GetChatCompletionsAsync</c>.
    /// </summary>
    /// <remarks>
    /// If <c>request.Stream == true</c> this method throws — use
    /// <see cref="CreateStreamAsync(ChatCompletionRequest, CancellationToken)"/> instead.
    /// </remarks>
    public Task<ChatCompletionResponse> CreateAsync(
        ChatCompletionRequest request,
        CancellationToken cancellationToken = default)
        => CreateAsync(request, sessionId: null, cancellationToken);

    /// <summary>
    /// Send a non-streaming chat completion request through the Shashu
    /// security pipeline.
    /// </summary>
    /// <param name="request">The completion request.</param>
    /// <param name="sessionId">
    /// Per-request session. An instruction that wins over a captured inbound
    /// session and over this client's construction-time default, and applies
    /// to this request only.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<ChatCompletionResponse> CreateAsync(
        ChatCompletionRequest request,
        string? sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Stream == true)
        {
            throw new InvalidOperationException(
                "request.Stream is true — call CreateStreamAsync(...) instead of CreateAsync(...).");
        }
        return _api.PostAsync<ChatCompletionRequest, ChatCompletionResponse>(
            "/v1/chat/completions", request, cancellationToken, sessionId);
    }

    /// <summary>
    /// Send a streaming chat completion request, yielding
    /// <see cref="ChatCompletionChunk"/> values parsed from the response's
    /// Server-Sent Events <c>data:</c> frames. The full assistant message is
    /// the concatenation of <c>chunk.Choices[0].Delta.Content</c>.
    /// </summary>
    /// <remarks>
    /// Cancelling the <paramref name="cancellationToken"/> aborts the call
    /// and disposes the underlying HTTP connection.
    /// </remarks>
    public IAsyncEnumerable<ChatCompletionChunk> CreateStreamAsync(
        ChatCompletionRequest request,
        CancellationToken cancellationToken = default)
        => CreateStreamAsync(request, sessionId: null, cancellationToken);

    /// <summary>
    /// Send a streaming chat completion request.
    /// </summary>
    /// <param name="request">The completion request.</param>
    /// <param name="sessionId">
    /// Per-request session. An instruction that wins over a captured inbound
    /// session and over this client's construction-time default, and applies
    /// to this request only.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public IAsyncEnumerable<ChatCompletionChunk> CreateStreamAsync(
        ChatCompletionRequest request,
        string? sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        // Force stream=true server-side so the gateway opens an SSE channel
        // even if the caller forgot to set it. Idempotent if already true.
        var streamingRequest = request with { Stream = true };
        return _api.PostStreamAsync(
            "/v1/chat/completions", streamingRequest, cancellationToken, sessionId);
    }
}
