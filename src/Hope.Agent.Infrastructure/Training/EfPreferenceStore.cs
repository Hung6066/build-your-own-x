using Hope.Agent.Application.Training;
using Hope.Agent.Domain.Training;
using Hope.Agent.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Hope.Agent.Infrastructure.Training;

internal sealed class EfPreferenceStore(AgentDbContext db) : IPreferenceStore
{
    public async Task AddAsync(PreferenceRecord record, CancellationToken ct)
    {
        await db.PreferenceRecords.AddAsync(record, ct);
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<PreferenceRecord>> QueryAsync(
        DateTimeOffset? since,
        DateTimeOffset? until,
        string? specialty,
        int take,
        CancellationToken ct)
    {
        var q = db.PreferenceRecords.AsNoTracking();
        if (since is not null) q = q.Where(r => r.CreatedAt >= since.Value);
        if (until is not null) q = q.Where(r => r.CreatedAt <= until.Value);
        if (!string.IsNullOrWhiteSpace(specialty)) q = q.Where(r => r.Specialty == specialty);
        return await q.OrderByDescending(r => r.CreatedAt).Take(take).ToListAsync(ct);
    }

    public async Task<int> CountAsync(DateTimeOffset? since, CancellationToken ct)
    {
        var q = db.PreferenceRecords.AsNoTracking();
        if (since is not null) q = q.Where(r => r.CreatedAt >= since.Value);
        return await q.CountAsync(ct);
    }
}
