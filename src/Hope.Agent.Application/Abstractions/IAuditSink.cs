using Hope.Agent.Domain.Audit;

namespace Hope.Agent.Application.Abstractions;

public interface IAuditSink
{
    Task WriteAsync(AuditEvent evt, CancellationToken ct);
}
