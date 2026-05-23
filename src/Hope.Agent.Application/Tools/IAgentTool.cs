using Hope.Agent.Application.LLM;

namespace Hope.Agent.Application.Tools;

public interface IAgentTool
{
    ToolDefinition Definition { get; }
    Task<string> InvokeAsync(string argumentsJson, ToolInvocationContext context, CancellationToken ct);
}

public sealed record ToolInvocationContext(Guid UserId, Guid ConversationId, string CorrelationId);

public interface IToolRegistry
{
    IReadOnlyList<IAgentTool> All { get; }
    IAgentTool? Find(string name);
    void Register(IAgentTool tool);
}
