using Hope.Agent.Domain.Security;

namespace Hope.Agent.Application.Security;

public sealed record ApprovalResult(bool Approved, string? Reason, Guid? DecidedBy, ToolApprovalStatus Status);

public sealed record ApprovalRequestInput(
    Guid ConversationId,
    Guid UserId,
    string ToolName,
    string ArgumentsJson,
    ToolImpactLevel Impact);

public interface IToolApprovalGate
{
    /// <summary>
    /// Persist a pending approval request, push to on-call clinicians, and wait for the decision.
    /// Default-denies after the configured timeout.
    /// </summary>
    Task<ApprovalResult> RequestAsync(ApprovalRequestInput input, CancellationToken ct);

    /// <summary>
    /// Release a waiting <see cref="RequestAsync"/> caller with an approve/deny decision.
    /// </summary>
    Task<bool> CompleteAsync(Guid requestId, bool approved, string? reason, Guid decidedBy, CancellationToken ct);
}
