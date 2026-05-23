namespace Hope.Agent.Application.LLM;

public sealed record ChatMessage(string Role, string Content, string? Name = null, string? ToolCallId = null);

public sealed record ToolDefinition(string Name, string Description, string ParametersJsonSchema);

public sealed record ToolCall(string Id, string Name, string ArgumentsJson);

public sealed record ChatRequest(
    IReadOnlyList<ChatMessage> Messages,
    string? Model = null,
    float Temperature = 0.2f,
    int? MaxTokens = null,
    IReadOnlyList<ToolDefinition>? Tools = null,
    string? ToolChoice = null,
    string? UserId = null);

public sealed record ChatUsage(int PromptTokens, int CompletionTokens, int TotalTokens);

public sealed record ChatResponse(
    string Content,
    IReadOnlyList<ToolCall> ToolCalls,
    string FinishReason,
    ChatUsage Usage,
    string Provider,
    string Model);

public sealed record EmbeddingRequest(IReadOnlyList<string> Inputs, string? Model = null);

public sealed record EmbeddingResponse(IReadOnlyList<ReadOnlyMemory<float>> Vectors, string Provider, string Model, int TotalTokens);

public interface IChatCompletionProvider
{
    string Name { get; }
    Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken ct);
    IAsyncEnumerable<string> StreamAsync(ChatRequest request, CancellationToken ct);
}

public interface IEmbeddingProvider
{
    string Name { get; }
    Task<EmbeddingResponse> EmbedAsync(EmbeddingRequest request, CancellationToken ct);
}

public interface ILLMRouter
{
    IChatCompletionProvider SelectChat(string? hint = null);
    IEmbeddingProvider SelectEmbedding(string? hint = null);
}
