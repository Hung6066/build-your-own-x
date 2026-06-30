namespace Hope.Agent.Domain.Clinical;

public sealed class ReminderRecord
{
    public Guid Id { get; init; }
    public Guid? TenantId { get; set; }
    public string ReminderId { get; set; } = string.Empty;
    public Guid PatientId { get; set; }
    public Guid? UserId { get; set; }
    public string? WorkflowId { get; set; }
    public string ReminderType { get; set; } = "medication";
    public string MedicationName { get; set; } = string.Empty;
    public string? Dosage { get; set; }
    public string Frequency { get; set; } = "once_daily";
    public DateTimeOffset StartAt { get; set; }
    public int DurationDays { get; set; }
    public string PreferredChannel { get; set; } = "zalo";
    public int AdherenceRiskScore { get; set; }
    public string Status { get; set; } = "scheduled";
    public int ConfirmedCount { get; set; }
    public int MissedCount { get; set; }
    public DateTimeOffset? LastConfirmedAt { get; set; }
    public DateTimeOffset? LastMissedAt { get; set; }
    public string? EscalationReason { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string? CorrelationId { get; set; }
}
