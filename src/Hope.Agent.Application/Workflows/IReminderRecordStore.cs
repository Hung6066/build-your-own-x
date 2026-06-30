namespace Hope.Agent.Application.Workflows;

public sealed record ReminderRecordWrite(
    string ReminderId,
    Guid PatientId,
    Guid UserId,
    string? WorkflowId,
    string ReminderType,
    string MedicationName,
    string? Dosage,
    string Frequency,
    DateTimeOffset StartAt,
    int DurationDays,
    string PreferredChannel,
    int AdherenceRiskScore,
    string Status,
    DateTimeOffset CreatedAt,
    string? CorrelationId,
    Guid? TenantId = null);

public sealed record ReminderStatusWrite(
    string ReminderId,
    string Status,
    int? ConfirmedCount,
    int? MissedCount,
    DateTimeOffset? LastConfirmedAt,
    DateTimeOffset? LastMissedAt,
    string? EscalationReason,
    DateTimeOffset UpdatedAt);

public interface IReminderRecordStore
{
    Task SaveAsync(ReminderRecordWrite reminder, CancellationToken ct = default);
    Task UpdateStatusAsync(ReminderStatusWrite status, CancellationToken ct = default);
}
