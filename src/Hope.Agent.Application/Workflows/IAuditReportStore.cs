namespace Hope.Agent.Application.Workflows;

public sealed record AuditReportWrite(
    string ReportId,
    Guid RequestedBy,
    string ReportType,
    DateTimeOffset? PeriodStart,
    DateTimeOffset? PeriodEnd,
    string Narrative,
    string? MetricsJson,
    string? AnomaliesJson,
    string Format,
    string ExportPath,
    string IntegrityHash,
    int ByteSize,
    string SigningAlgorithm,
    DateTimeOffset ExportedAt,
    string Status,
    string? CorrelationId);

public interface IAuditReportStore
{
    Task SaveAsync(AuditReportWrite report, CancellationToken ct = default);
}
