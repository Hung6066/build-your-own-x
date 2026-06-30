namespace Hope.Agent.Domain.Appointments;

public sealed class OptimizationCostHint
{
    public Guid Id { get; init; }
    public string DoctorId { get; set; } = string.Empty;
    public string Specialty { get; set; } = string.Empty;
    public double SuccessRate { get; set; } = 0.85;
    public long Samples { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
