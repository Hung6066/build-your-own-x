namespace Hope.Agent.Application.Agents;

public sealed record AgentRequest(
    Guid UserId,
    Guid? ConversationId,
    string Message,
    string? AgentProfile = null,
    string? CorrelationId = null,
    bool Stream = false,
    IReadOnlyList<string>? Roles = null);

public sealed record AgentResponse(
    Guid ConversationId,
    string Reply,
    IReadOnlyList<AgentToolExecution> ToolExecutions,
    int PromptTokens,
    int CompletionTokens,
    string Provider,
    string Model,
    TimeSpan Duration,
    decimal CostUsd = 0m);

public sealed record AgentToolExecution(string Tool, string ArgumentsJson, string ResultJson, TimeSpan Duration, bool Success);

public interface IAgentRuntime
{
    Task<AgentResponse> RunAsync(AgentRequest request, CancellationToken ct);
    IAsyncEnumerable<string> StreamAsync(AgentRequest request, CancellationToken ct);
}
