using Hope.Agent.Domain.Conversations;
using Hope.Agent.Domain.Autonomy;
using Hope.Agent.Application.Governance;
using Hope.Agent.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace Hope.Agent.Api.Endpoints;

/// <summary>Read-only admin aggregator endpoints designed to back a Blazor dashboard.</summary>
public static class DashboardEndpoints
{
    public static IEndpointRouteBuilder MapDashboardEndpoints(this IEndpointRouteBuilder app)
    {
        var grp = app.MapGroup("/v1/dashboard").RequireAuthorization("TenantAccess").WithTags("Dashboard");

        grp.MapGet("/overview", async (IReadOnlyAgentDbContextFactory dbFactory, CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateAsync(ct);
            var since = DateTimeOffset.UtcNow.AddDays(-7);
            var convs7d = await db.Conversations.AsNoTracking().Where(c => c.CreatedAt >= since).CountAsync(ct);
            var msgs7d = await db.Messages.AsNoTracking().Where(m => m.CreatedAt >= since).CountAsync(ct);
            var pendingApprovals = await db.ToolApprovalRequests.AsNoTracking().CountAsync(t => t.Status == Domain.Security.ToolApprovalStatus.Pending, ct);
            var skills = await db.LearnedSkills.AsNoTracking().CountAsync(ct);
            var blockedPatterns = await db.AdversarialPatterns.AsNoTracking().CountAsync(p => p.Active, ct);
            return Results.Ok(new
            {
                window_days = 7,
                conversations_7d = convs7d,
                messages_7d = msgs7d,
                pending_approvals = pendingApprovals,
                learned_skills = skills,
                active_adversarial_patterns = blockedPatterns,
            });
        });

        grp.MapGet("/conversations", async (
            IReadOnlyAgentDbContextFactory dbFactory,
            Guid? userId,
            int? take,
            CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateAsync(ct);
            var q = db.Conversations.AsNoTracking();
            if (userId is Guid u) q = q.Where(c => c.UserId == u);
            var rows = await q.OrderByDescending(c => c.UpdatedAt)
                .Take(Math.Clamp(take ?? 50, 1, 200))
                .Select(c => new
                {
                    c.Id,
                    c.UserId,
                    c.Title,
                    c.UpdatedAt,
                    Messages = c.Messages.Count,
                })
                .ToListAsync(ct);
            return Results.Ok(rows);
        });

        grp.MapGet("/conversations/{id:guid}", async (Guid id, IReadOnlyAgentDbContextFactory dbFactory, CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateAsync(ct);
            var conv = await db.Conversations.AsNoTracking()
                .Where(c => c.Id == id)
                .Select(c => new
                {
                    c.Id,
                    c.UserId,
                    c.Title,
                    c.CreatedAt,
                    c.UpdatedAt,
                    Messages = c.Messages.OrderBy(m => m.CreatedAt).Select(m => new
                    {
                        m.Id,
                        m.Role,
                        m.Content,
                        m.ToolName,
                        m.CreatedAt,
                    }).ToList(),
                })
                .FirstOrDefaultAsync(ct);
            return conv is null ? Results.NotFound() : Results.Ok(conv);
        });

        grp.MapGet("/audit", async (IReadOnlyAgentDbContextFactory dbFactory, int? take, CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateAsync(ct);
            var rows = await db.AuditEvents.AsNoTracking()
                .OrderByDescending(a => a.OccurredAt)
                .Take(Math.Clamp(take ?? 100, 1, 500))
                .ToListAsync(ct);
            return Results.Ok(rows);
        });

        grp.MapGet("/audit-cursor", async (
            IReadOnlyAgentDbContextFactory dbFactory,
            Guid? tenantId,
            DateTimeOffset? cursorOccurredAt,
            Guid? cursorId,
            int? take,
            CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateAsync(ct);
            var pageSize = Math.Clamp(take ?? 100, 1, 500);
            var q = db.AuditEvents.AsNoTracking();
            if (tenantId is { } t) q = q.Where(x => x.TenantId == t);
            if (cursorOccurredAt is { } ts && cursorId is { } id)
                q = q.Where(x => x.OccurredAt < ts || (x.OccurredAt == ts && x.Id.CompareTo(id) < 0));

            var rows = await q.OrderByDescending(x => x.OccurredAt).ThenByDescending(x => x.Id)
                .Take(pageSize + 1)
                .ToListAsync(ct);
            var page = rows.Take(pageSize).ToList();
            var last = page.LastOrDefault();
            return Results.Ok(new
            {
                items = page,
                nextCursor = rows.Count > pageSize && last is not null
                    ? new { occurredAt = last.OccurredAt, id = last.Id }
                    : null,
            });
        });

        grp.MapGet("/autonomy/decisions-cursor", async (
            IReadOnlyAgentDbContextFactory dbFactory,
            Guid? tenantId,
            Guid? patientId,
            DateTimeOffset? cursorCreatedAt,
            Guid? cursorId,
            int? take,
            CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateAsync(ct);
            var pageSize = Math.Clamp(take ?? 100, 1, 500);
            var q = db.AgentDecisions.AsNoTracking();
            if (tenantId is { } t) q = q.Where(x => x.TenantId == t);
            if (patientId is { } p) q = q.Where(x => x.PatientId == p);
            if (cursorCreatedAt is { } ts && cursorId is { } id)
                q = q.Where(x => x.CreatedAt < ts || (x.CreatedAt == ts && x.Id.CompareTo(id) < 0));

            var rows = await q.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id)
                .Take(pageSize + 1)
                .ToListAsync(ct);
            var page = rows.Take(pageSize).ToList();
            var last = page.LastOrDefault();
            return Results.Ok(new
            {
                items = page,
                nextCursor = rows.Count > pageSize && last is not null
                    ? new { createdAt = last.CreatedAt, id = last.Id }
                    : null,
            });
        });

        grp.MapGet("/autonomy/actions-cursor", async (
            IReadOnlyAgentDbContextFactory dbFactory,
            Guid? tenantId,
            AutonomousActionStatus? status,
            DateTimeOffset? cursorCreatedAt,
            Guid? cursorId,
            int? take,
            CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateAsync(ct);
            var pageSize = Math.Clamp(take ?? 100, 1, 500);
            var q = db.AutonomousActions.AsNoTracking();
            if (tenantId is { } t) q = q.Where(x => x.TenantId == t);
            if (status is { } s) q = q.Where(x => x.Status == s);
            if (cursorCreatedAt is { } ts && cursorId is { } id)
                q = q.Where(x => x.CreatedAt < ts || (x.CreatedAt == ts && x.Id.CompareTo(id) < 0));

            var rows = await q.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id)
                .Take(pageSize + 1)
                .ToListAsync(ct);
            var page = rows.Take(pageSize).ToList();
            var last = page.LastOrDefault();
            return Results.Ok(new
            {
                items = page,
                nextCursor = rows.Count > pageSize && last is not null
                    ? new { createdAt = last.CreatedAt, id = last.Id }
                    : null,
            });
        });

        grp.MapGet("/skills", async (IReadOnlyAgentDbContextFactory dbFactory, int? take, CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateAsync(ct);
            var rows = await db.LearnedSkills.AsNoTracking()
                .OrderByDescending(s => s.LastUsed)
                .Take(Math.Clamp(take ?? 50, 1, 200))
                .ToListAsync(ct);
            return Results.Ok(rows);
        });

        grp.MapGet("/scale", async (
            IReadOnlyAgentDbContextFactory dbFactory,
            IOptionsMonitor<RuntimeScaleOptions> scale,
            IOptionsMonitor<AgentOpsOptions> ops,
            IOptionsMonitor<TenantIsolationOptions> tenantIsolation,
            IOptionsMonitor<CostControlOptions> costControl,
            IOptionsMonitor<DataLifecycleOptions> dataLifecycle,
            IOptionsMonitor<DeploymentSafetyOptions> deploymentSafety,
            CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateAsync(ct);
            var opts = scale.CurrentValue;
            var now = DateTimeOffset.UtcNow;
            var pendingActions = await db.AutonomousActions.AsNoTracking().CountAsync(x => x.Status == Domain.Autonomy.AutonomousActionStatus.Pending || x.Status == Domain.Autonomy.AutonomousActionStatus.Approved, ct);
            var failedActionsHour = await db.AutonomousActions.AsNoTracking().CountAsync(x => x.CreatedAt >= now.AddHours(-1) && x.Status == Domain.Autonomy.AutonomousActionStatus.Failed, ct);
            var pendingApprovals = await db.ToolApprovalRequests.AsNoTracking().CountAsync(x => x.Status == Domain.Security.ToolApprovalStatus.Pending, ct);
            var latestGate = await db.AutonomyEvalGateRuns.AsNoTracking()
                .OrderByDescending(x => x.CreatedAt)
                .Select(x => new { x.GateId, x.Passed, x.PassRate, x.CreatedAt })
                .FirstOrDefaultAsync(ct);
            var latestDrift = await db.AutonomyDriftSignals.AsNoTracking()
                .OrderByDescending(x => x.CreatedAt)
                .Select(x => new { x.SignalId, x.Severity, x.Score, x.CreatedAt })
                .FirstOrDefaultAsync(ct);
            var queueStatus = pendingActions >= opts.QueueBacklogWarningThreshold ? "warning" : "ok";
            var approvalStatus = pendingApprovals >= opts.ApprovalBacklogWarningThreshold ? "warning" : "ok";
            return Results.Ok(new
            {
                runtime = opts,
                tenantIsolation = tenantIsolation.CurrentValue,
                costControl = costControl.CurrentValue,
                dataLifecycle = dataLifecycle.CurrentValue,
                deploymentSafety = deploymentSafety.CurrentValue,
                queues = new
                {
                    pendingActions,
                    failedActionsHour,
                    pendingApprovals,
                    queueStatus,
                    approvalStatus,
                    durableQueueBackend = opts.DurableQueueBackend,
                    ledgerQueueBackend = opts.LedgerQueueBackend,
                    postgresQueueHighThroughputAllowed = opts.PostgresQueueHighThroughputAllowed,
                },
                level5 = new
                {
                    latestGate = latestGate is null ? null : new { latestGate.GateId, latestGate.Passed, latestGate.PassRate, latestGate.CreatedAt },
                    latestDrift = latestDrift is null ? null : new { latestDrift.SignalId, latestDrift.Severity, latestDrift.Score, latestDrift.CreatedAt },
                },
                alerting = new
                {
                    ops.CurrentValue.AlertChannel,
                    ops.CurrentValue.AlertRules,
                },
            });
        });

        grp.MapGet("/cost", async (IReadOnlyAgentDbContextFactory dbFactory, int? days, CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateAsync(ct);
            var since = DateTimeOffset.UtcNow.AddDays(-Math.Clamp(days ?? 7, 1, 90));
            var rows = await db.AuditEvents.AsNoTracking()
                .Where(x => x.Action == "agent.run" && x.OccurredAt >= since)
                .Select(x => x.PayloadJson)
                .ToListAsync(ct);
            decimal total = 0;
            var byModel = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
            var byTenant = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
            foreach (var json in rows)
            {
                try
                {
                    using var doc = JsonDocument.Parse(json ?? "{}");
                    var root = doc.RootElement;
                    var model = root.TryGetProperty("model", out var m) ? m.GetString() ?? "unknown" : "unknown";
                    var tenant = root.TryGetProperty("tenantId", out var t) ? t.GetString() ?? "unknown" : "unknown";
                    var cost = root.TryGetProperty("costUsd", out var c) && c.TryGetDecimal(out var usd) ? usd : 0m;
                    total += cost;
                    byModel[model] = byModel.GetValueOrDefault(model) + cost;
                    byTenant[tenant] = byTenant.GetValueOrDefault(tenant) + cost;
                }
                catch { }
            }
            return Results.Ok(new { windowDays = Math.Clamp(days ?? 7, 1, 90), totalCostUsd = total, runs = rows.Count, byModel, byTenant });
        });

        grp.MapGet("/agent-registry", (
            IOptionsMonitor<AgentRegistryOptions> registry,
            IOptionsMonitor<AgentOwnershipOptions> ownership) =>
        {
            return Results.Ok(new { registry = registry.CurrentValue, ownership = ownership.CurrentValue });
        });

        return app;
    }
}
