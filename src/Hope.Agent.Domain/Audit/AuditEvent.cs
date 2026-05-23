namespace Hope.Agent.Domain.Audit;

public sealed class AuditEvent
{
    public Guid Id { get; init; }
    public DateTimeOffset OccurredAt { get; init; }
    public Guid? UserId { get; init; }
    public string Actor { get; init; } = string.Empty;
    public string Action { get; init; } = string.Empty;
    public string? ResourceType { get; init; }
    public string? ResourceId { get; init; }
    public string? PatientId { get; init; }
    public string? CorrelationId { get; init; }
    public string? Reason { get; init; }
    public string PayloadJson { get; init; } = "{}";
}
