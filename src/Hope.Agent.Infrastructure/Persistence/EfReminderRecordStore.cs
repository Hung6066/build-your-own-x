using Hope.Agent.Application.Workflows;
using Hope.Agent.Application.Security;
using Hope.Agent.Domain.Clinical;
using Microsoft.EntityFrameworkCore;

namespace Hope.Agent.Infrastructure.Persistence;

public sealed class EfReminderRecordStore(IDbContextFactory<AgentDbContext> dbFactory) : IReminderRecordStore
{
    public async Task SaveAsync(ReminderRecordWrite reminder, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var entity = await db.ReminderRecords
            .SingleOrDefaultAsync(x => x.ReminderId == reminder.ReminderId, ct)
            .ConfigureAwait(false);

        if (entity is null)
        {
            entity = new ReminderRecord
            {
                Id = Guid.CreateVersion7(),
                ReminderId = reminder.ReminderId,
                CreatedAt = reminder.CreatedAt,
            };
            db.ReminderRecords.Add(entity);
        }

        entity.PatientId = reminder.PatientId;
        entity.TenantId = reminder.TenantId ?? SecurityDefaults.DefaultTenantId;
        entity.UserId = reminder.UserId;
        entity.WorkflowId = reminder.WorkflowId;
        entity.ReminderType = reminder.ReminderType;
        entity.MedicationName = reminder.MedicationName;
        entity.Dosage = reminder.Dosage;
        entity.Frequency = reminder.Frequency;
        entity.StartAt = reminder.StartAt;
        entity.DurationDays = reminder.DurationDays;
        entity.PreferredChannel = reminder.PreferredChannel;
        entity.AdherenceRiskScore = reminder.AdherenceRiskScore;
        entity.Status = reminder.Status;
        entity.UpdatedAt = reminder.CreatedAt;
        entity.CorrelationId = reminder.CorrelationId;

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task UpdateStatusAsync(ReminderStatusWrite status, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var entity = await db.ReminderRecords
            .SingleOrDefaultAsync(x => x.ReminderId == status.ReminderId, ct)
            .ConfigureAwait(false);

        if (entity is null)
            return;

        entity.Status = status.Status;
        if (status.ConfirmedCount.HasValue) entity.ConfirmedCount = status.ConfirmedCount.Value;
        if (status.MissedCount.HasValue) entity.MissedCount = status.MissedCount.Value;
        if (status.LastConfirmedAt.HasValue) entity.LastConfirmedAt = status.LastConfirmedAt;
        if (status.LastMissedAt.HasValue) entity.LastMissedAt = status.LastMissedAt;
        if (!string.IsNullOrWhiteSpace(status.EscalationReason)) entity.EscalationReason = status.EscalationReason;
        entity.UpdatedAt = status.UpdatedAt;

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
