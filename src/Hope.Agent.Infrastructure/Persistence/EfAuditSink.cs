using Hope.Agent.Application.Abstractions;
using Hope.Agent.Domain.Audit;

namespace Hope.Agent.Infrastructure.Persistence;

internal sealed class EfAuditSink(AgentDbContext db) : IAuditSink
{
    public async Task WriteAsync(AuditEvent evt, CancellationToken ct)
    {
        await db.AuditEvents.AddAsync(evt, ct);
        await db.SaveChangesAsync(ct);
    }
}
