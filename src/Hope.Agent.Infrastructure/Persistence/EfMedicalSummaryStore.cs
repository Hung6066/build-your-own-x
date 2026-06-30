using Hope.Agent.Application.Workflows;
using Hope.Agent.Application.Security;
using Hope.Agent.Domain.Clinical;
using Microsoft.EntityFrameworkCore;

namespace Hope.Agent.Infrastructure.Persistence;

public sealed class EfMedicalSummaryStore(IDbContextFactory<AgentDbContext> dbFactory) : IMedicalSummaryStore
{
    public async Task SaveAsync(MedicalSummaryWrite summary, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var entity = await db.MedicalSummaries
            .SingleOrDefaultAsync(x => x.SummaryId == summary.SummaryId, ct)
            .ConfigureAwait(false);

        if (entity is null)
        {
            entity = new MedicalSummaryRecord
            {
                Id = Guid.CreateVersion7(),
                SummaryId = summary.SummaryId,
                CreatedAt = summary.CreatedAt,
            };
            db.MedicalSummaries.Add(entity);
        }

        entity.PatientId = summary.PatientId;
        entity.TenantId = summary.TenantId ?? SecurityDefaults.DefaultTenantId;
        entity.UserId = summary.UserId;
        entity.SummaryType = summary.SummaryType;
        entity.Audience = summary.Audience;
        entity.Specialty = summary.Specialty;
        entity.SourceContext = summary.SourceContext;
        entity.SummaryText = summary.SummaryText;
        entity.Model = summary.Model;
        entity.Status = summary.Status;
        entity.UpdatedAt = summary.CreatedAt;
        entity.CorrelationId = summary.CorrelationId;

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
