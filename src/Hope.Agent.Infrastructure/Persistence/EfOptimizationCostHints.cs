using Hope.Agent.Application.Tools;
using Hope.Agent.Domain.Appointments;
using Microsoft.EntityFrameworkCore;

namespace Hope.Agent.Infrastructure.Persistence;

public sealed class EfOptimizationCostHints(IDbContextFactory<AgentDbContext> dbFactory) : IOptimizationCostHints
{
    private const double Alpha = 0.3;

    public async Task RecordOutcomeAsync(string doctorId, string specialty, bool succeeded, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var entity = await db.OptimizationCostHints
            .SingleOrDefaultAsync(x => x.DoctorId == doctorId && x.Specialty == specialty, ct)
            .ConfigureAwait(false);

        var sample = succeeded ? 1.0 : 0.0;
        if (entity is null)
        {
            entity = new OptimizationCostHint
            {
                Id = Guid.CreateVersion7(),
                DoctorId = doctorId,
                Specialty = specialty,
                SuccessRate = sample,
                Samples = 1,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            db.OptimizationCostHints.Add(entity);
        }
        else
        {
            entity.SuccessRate = entity.SuccessRate + Alpha * (sample - entity.SuccessRate);
            entity.Samples++;
            entity.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<double> GetSuccessRateAsync(string doctorId, string specialty, double defaultRate = 0.85, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var rate = await db.OptimizationCostHints
            .AsNoTracking()
            .Where(x => x.DoctorId == doctorId && x.Specialty == specialty)
            .Select(x => (double?)x.SuccessRate)
            .SingleOrDefaultAsync(ct)
            .ConfigureAwait(false);

        return rate ?? defaultRate;
    }
}
