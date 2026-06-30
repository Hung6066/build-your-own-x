namespace Hope.Agent.Application.Workflows;

public sealed record MedicalSummaryWrite(
    string SummaryId,
    Guid? PatientId,
    Guid UserId,
    string SummaryType,
    string Audience,
    string? Specialty,
    string SourceContext,
    string SummaryText,
    string? Model,
    string Status,
    DateTimeOffset CreatedAt,
    string? CorrelationId,
    Guid? TenantId = null);

public interface IMedicalSummaryStore
{
    Task SaveAsync(MedicalSummaryWrite summary, CancellationToken ct = default);
}
