namespace Hope.Agent.Domain.Audit;

public sealed class AuditReportRecord
{
    public Guid Id { get; init; }
    public string ReportId { get; set; } = string.Empty;
    public Guid RequestedBy { get; set; }
    public string ReportType { get; set; } = "operational";
    public DateTimeOffset? PeriodStart { get; set; }
    public DateTimeOffset? PeriodEnd { get; set; }
    public string Narrative { get; set; } = string.Empty;
    public string? MetricsJson { get; set; }
    public string? AnomaliesJson { get; set; }
    public string Format { get; set; } = "json";
    public string ExportPath { get; set; } = string.Empty;
    public string IntegrityHash { get; set; } = string.Empty;
    public int ByteSize { get; set; }
    public string SigningAlgorithm { get; set; } = "SHA-256";
    public DateTimeOffset ExportedAt { get; set; }
    public string Status { get; set; } = "exported";
    public string? CorrelationId { get; set; }
}
