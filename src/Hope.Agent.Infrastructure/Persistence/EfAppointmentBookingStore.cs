using Hope.Agent.Application.Workflows;
using Hope.Agent.Application.Security;
using Hope.Agent.Domain.Appointments;
using Microsoft.EntityFrameworkCore;

namespace Hope.Agent.Infrastructure.Persistence;

public sealed class EfAppointmentBookingStore(IDbContextFactory<AgentDbContext> dbFactory) : IAppointmentBookingStore
{
    public async Task SaveAsync(AppointmentBookingWrite booking, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var entity = await db.AppointmentBookings
            .SingleOrDefaultAsync(x => x.BookingId == booking.BookingId, ct)
            .ConfigureAwait(false);

        if (entity is null)
        {
            entity = new AppointmentBooking
            {
                Id = Guid.CreateVersion7(),
                BookingId = booking.BookingId,
            };
            db.AppointmentBookings.Add(entity);
        }

        entity.PatientId = booking.PatientId;
        entity.TenantId = booking.TenantId ?? SecurityDefaults.DefaultTenantId;
        entity.UserId = booking.UserId;
        entity.DoctorId = booking.DoctorId;
        entity.SlotId = booking.SlotId;
        entity.Reason = booking.Reason;
        entity.AppointmentTime = booking.AppointmentTime;
        entity.Status = booking.Status;
        entity.ConfirmedAt = booking.ConfirmedAt;
        entity.CorrelationId = booking.CorrelationId;

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
