namespace Hope.Agent.Domain.Clinical;

public sealed class MedicalSummaryRecord
{
    public Guid Id { get; init; }
    public Guid? TenantId { get; set; }
    public string SummaryId { get; set; } = string.Empty;
    public Guid? PatientId { get; set; }
    public Guid? UserId { get; set; }
    public string SummaryType { get; set; } = "soap";
    public string Audience { get; set; } = "clinician";
    public string? Specialty { get; set; }
    public string SourceContext { get; set; } = string.Empty;
    public string SummaryText { get; set; } = string.Empty;
    public string? Model { get; set; }
    public string Status { get; set; } = "completed";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string? CorrelationId { get; set; }
}
