namespace Hope.Agent.Domain.Security;

public enum ToolApprovalStatus
{
    Pending = 0,
    Approved = 1,
    Denied = 2,
    TimedOut = 3,
}

public sealed class ToolApprovalRequest
{
    public Guid Id { get; init; }
    public Guid ConversationId { get; init; }
    public Guid UserId { get; init; }
    public string ToolName { get; init; } = string.Empty;
    public string ArgumentsJson { get; init; } = "{}";
    public ToolImpactLevel Impact { get; init; }
    public DateTimeOffset RequestedAt { get; init; }
    public ToolApprovalStatus Status { get; set; } = ToolApprovalStatus.Pending;
    public Guid? DecidedBy { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public string? Reason { get; set; }
}
