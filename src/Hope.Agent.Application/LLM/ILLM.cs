namespace Hope.Agent.Application.LLM;

public sealed record ChatMessage(string Role, string Content, string? Name = null, string? ToolCallId = null);

public sealed record ToolDefinition(string Name, string Description, string ParametersJsonSchema);

public sealed record ToolCall(string Id, string Name, string ArgumentsJson);

/// <summary>
/// Constrains the model output. Closes a class of parse failures and prompt-injection vectors
/// (model cannot emit instructions outside a strict JSON Schema).
/// </summary>
/// <param name="Type">One of <c>text</c>, <c>json_object</c>, <c>json_schema</c>.</param>
/// <param name="JsonSchema">Raw JSON Schema (Draft-07 or 2020-12) when <see cref="Type"/> is <c>json_schema</c>.</param>
/// <param name="SchemaName">Human-readable schema name; required by OpenAI strict mode.</param>
/// <param name="Strict">Require the provider to enforce schema (OpenAI strict mode).</param>
public sealed record ChatResponseFormat(
    string Type,
    string? JsonSchema = null,
    string? SchemaName = null,
    bool Strict = true);

public sealed record ChatRequest(
    IReadOnlyList<ChatMessage> Messages,
    string? Model = null,
    float Temperature = 0.2f,
    int? MaxTokens = null,
    IReadOnlyList<ToolDefinition>? Tools = null,
    string? ToolChoice = null,
    string? UserId = null,
    ChatResponseFormat? ResponseFormat = null);

public sealed record ChatUsage(
    int PromptTokens,
    int CompletionTokens,
    int TotalTokens,
    decimal CostUsd = 0m);

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
