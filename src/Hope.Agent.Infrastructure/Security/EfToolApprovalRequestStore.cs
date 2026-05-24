using Hope.Agent.Application.Security;
using Hope.Agent.Domain.Security;
using Hope.Agent.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Hope.Agent.Infrastructure.Security;

internal sealed class EfToolApprovalRequestStore(AgentDbContext db) : IToolApprovalRequestStore
{
    public async Task AddAsync(ToolApprovalRequest request, CancellationToken ct)
    {
        db.ToolApprovalRequests.Add(request);
        await db.SaveChangesAsync(ct);
    }

    public Task<ToolApprovalRequest?> GetAsync(Guid id, CancellationToken ct) =>
        db.ToolApprovalRequests.FirstOrDefaultAsync(x => x.Id == id, ct);

    public async Task<IReadOnlyList<ToolApprovalRequest>> PendingAsync(int take, CancellationToken ct) =>
        await db.ToolApprovalRequests.AsNoTracking()
            .Where(x => x.Status == ToolApprovalStatus.Pending)
            .OrderByDescending(x => x.RequestedAt)
            .Take(take)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<ToolApprovalRequest>> QueryAsync(DateTimeOffset from, DateTimeOffset to, int take, CancellationToken ct) =>
        await db.ToolApprovalRequests.AsNoTracking()
            .Where(x => x.RequestedAt >= from && x.RequestedAt <= to)
            .OrderByDescending(x => x.RequestedAt)
            .Take(take)
            .ToListAsync(ct);

    public async Task UpdateAsync(ToolApprovalRequest request, CancellationToken ct)
    {
        db.ToolApprovalRequests.Update(request);
        await db.SaveChangesAsync(ct);
    }
}
