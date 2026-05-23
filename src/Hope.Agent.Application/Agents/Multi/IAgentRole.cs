namespace Hope.Agent.Application.Agents.Multi;

/// <summary>
/// A specialized role in the multi-agent system (e.g. clinical reasoning, scheduling, billing).
/// </summary>
public interface IAgentRole
{
    string Name { get; }
    string Description { get; }

    /// <summary>Capabilities/intents this role can handle (used by router for fast dispatch).</summary>
    IReadOnlyList<string> Intents { get; }

    Task<AgentRoleResult> HandleAsync(AgentTask task, CancellationToken ct);
}

public sealed record AgentTask(
    Guid TaskId,
    Guid UserId,
    string Intent,
    string Input,
    Dictionary<string, string> Context,
    Guid? ConversationId = null,
    string? CorrelationId = null,
    int Priority = 5);

public sealed record AgentRoleResult(
    string Role,
    bool Success,
    string Output,
    Dictionary<string, string>? Metadata = null,
    IReadOnlyList<AgentHandoff>? Handoffs = null);

public sealed record AgentHandoff(string TargetRole, string Reason, string Payload);

/// <summary>
/// Top-level orchestrator (Chief Medical Agent). Routes a task to one or more <see cref="IAgentRole"/> instances.
/// </summary>
public interface IMultiAgentOrchestrator
{
    Task<MultiAgentResult> DispatchAsync(AgentTask task, CancellationToken ct);
}

public sealed record MultiAgentResult(
    Guid TaskId,
    string FinalRole,
    string Output,
    IReadOnlyList<AgentRoleResult> Trace);
