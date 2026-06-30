namespace Hope.Agent.Application.Workflows;

public sealed record AppointmentBookingWrite(
    string BookingId,
    Guid? PatientId,
    Guid UserId,
    string DoctorId,
    string SlotId,
    string? Reason,
    DateTimeOffset? AppointmentTime,
    string Status,
    DateTimeOffset ConfirmedAt,
    string? CorrelationId,
    Guid? TenantId = null);

public interface IAppointmentBookingStore
{
    Task SaveAsync(AppointmentBookingWrite booking, CancellationToken ct = default);
}
