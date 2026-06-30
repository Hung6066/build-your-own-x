using Hope.Agent.Application.Abstractions;
using Hope.Agent.Application.Eventing;
using Hope.Agent.Application.Security;
using Hope.Agent.Domain.Audit;
using Hope.Agent.Infrastructure.Eventing;
using System.Text.Json;

namespace Hope.Agent.Infrastructure.Persistence;

internal sealed class EfAuditSink(AgentDbContext db) : IAuditSink
{
    public async Task WriteAsync(AuditEvent evt, CancellationToken ct)
    {
        if (evt.TenantId is null)
        {
            evt = WithTenant(evt, SecurityDefaults.DefaultTenantId);
        }
        await db.AuditEvents.AddAsync(evt, ct);
        await db.OutboxEvents.AddAsync(EfOutboxStore.ToEntity(new OutboxEventWrite(
            evt.TenantId,
            "hope.audit.events",
            evt.Id.ToString(),
            JsonSerializer.Serialize(new
            {
                evt.Id,
                evt.TenantId,
                evt.Actor,
                evt.Action,
                evt.ResourceType,
                evt.ResourceId,
                evt.PatientId,
                evt.OccurredAt,
                evt.CorrelationId,
            }),
            CorrelationId: evt.CorrelationId,
            IdempotencyKey: $"audit:{evt.Id}")), ct);
        await db.SaveChangesAsync(ct);
    }

    private static AuditEvent WithTenant(AuditEvent evt, Guid tenantId) => new()
    {
        Id = evt.Id,
        TenantId = tenantId,
        OccurredAt = evt.OccurredAt,
        UserId = evt.UserId,
        Actor = evt.Actor,
        Action = evt.Action,
        ResourceType = evt.ResourceType,
        ResourceId = evt.ResourceId,
        PatientId = evt.PatientId,
        CorrelationId = evt.CorrelationId,
        Reason = evt.Reason,
        DeploymentVersion = evt.DeploymentVersion,
        PromptVersion = evt.PromptVersion,
        ModelVersion = evt.ModelVersion,
        ToolsetVersion = evt.ToolsetVersion,
        PolicyVersion = evt.PolicyVersion,
        PayloadJson = evt.PayloadJson,
    };
}
