using Hope.Agent.Application.Learning;
using Hope.Agent.Domain.Learning;
using Hope.Agent.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Hope.Agent.Infrastructure.Learning;

internal sealed class EfEvalCaseStore(AgentDbContext db) : IEvalCaseStore
{
    public async Task<IReadOnlyList<EvalCase>> GetBySuiteAsync(string suite, CancellationToken ct)
    {
        return await db.EvalCases.AsNoTracking()
            .Where(c => c.Suite == suite && c.Active)
            .OrderBy(c => c.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task<EvalCase> AddAsync(EvalCase evalCase, CancellationToken ct)
    {
        db.EvalCases.Add(evalCase);
        await db.SaveChangesAsync(ct);
        return evalCase;
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
    {
        var row = await db.EvalCases.FindAsync([id], ct);
        if (row is null) return false;
        row.Active = false;
        await db.SaveChangesAsync(ct);
        return true;
    }
}
