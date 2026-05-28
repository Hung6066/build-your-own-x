namespace Hope.Agent.Application.Plans;

public enum PlanStepStatus { Pending, InProgress, Done, Failed, Skipped }

public sealed record PlanStep(
    string Id,
    string Title,
    PlanStepStatus Status,
    string? Result = null,
    DateTimeOffset? StartedAt = null,
    DateTimeOffset? CompletedAt = null);

public sealed record AgentPlan(
    Guid ConversationId,
    IReadOnlyList<PlanStep> Steps,
    DateTimeOffset UpdatedAt);

/// <summary>
/// Persistent plan / TaskTracker for long-horizon agent execution. Inspired by Claude Code,
/// Manus, and LangGraph supervisor patterns. Enables:
/// <list type="bullet">
///   <item>Resumption of multi-turn workflows across process restarts.</item>
///   <item>Clinician-facing transparency (render plan + step status).</item>
///   <item>Deterministic replay in eval / debugging.</item>
/// </list>
/// </summary>
public interface IAgentPlanTracker
{
    Task<AgentPlan?> GetAsync(Guid conversationId, CancellationToken ct);
    Task SaveAsync(AgentPlan plan, CancellationToken ct);
    Task<AgentPlan> UpdateStepAsync(Guid conversationId, string stepId,
        PlanStepStatus status, string? result, CancellationToken ct);
}

public sealed class NoOpAgentPlanTracker : IAgentPlanTracker
{
    public Task<AgentPlan?> GetAsync(Guid conversationId, CancellationToken ct)
        => Task.FromResult<AgentPlan?>(null);

    public Task SaveAsync(AgentPlan plan, CancellationToken ct) => Task.CompletedTask;

    public Task<AgentPlan> UpdateStepAsync(Guid conversationId, string stepId,
        PlanStepStatus status, string? result, CancellationToken ct)
        => Task.FromResult(new AgentPlan(conversationId, Array.Empty<PlanStep>(), DateTimeOffset.UtcNow));
}
