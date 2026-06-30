namespace Hope.Agent.Domain.Eventing;

public enum OutboxEventStatus
{
    Pending = 0,
    Publishing = 1,
    Published = 2,
    Failed = 3,
    DeadLetter = 4,
}

public sealed class OutboxEvent
{
    public Guid Id { get; set; }
    public Guid? TenantId { get; set; }
    public string Topic { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = "{}";
    public string HeadersJson { get; set; } = "{}";
    public OutboxEventStatus Status { get; set; }
    public int AttemptCount { get; set; }
    public int MaxAttempts { get; set; } = 5;
    public DateTimeOffset ScheduledFor { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    public string? LastError { get; set; }
    public string? CorrelationId { get; set; }
    public string? IdempotencyKey { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
