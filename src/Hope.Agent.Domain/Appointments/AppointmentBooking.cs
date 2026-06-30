namespace Hope.Agent.Domain.Appointments;

public sealed class AppointmentBooking
{
    public Guid Id { get; init; }
    public Guid? TenantId { get; set; }
    public string BookingId { get; set; } = string.Empty;
    public Guid? PatientId { get; set; }
    public Guid? UserId { get; set; }
    public string DoctorId { get; set; } = string.Empty;
    public string SlotId { get; set; } = string.Empty;
    public string? Reason { get; set; }
    public DateTimeOffset? AppointmentTime { get; set; }
    public string Status { get; set; } = "confirmed";
    public DateTimeOffset ConfirmedAt { get; set; }
    public string? CorrelationId { get; set; }
}
