using Hope.Agent.Application.Governance;
using Hope.Agent.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hope.Agent.Infrastructure.Maintenance;

internal sealed class ScaleMaintenanceWorker(
    IServiceScopeFactory scopeFactory,
    IOptionsMonitor<DatabaseScaleOptions> options,
    ILogger<ScaleMaintenanceWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var opts = options.CurrentValue;
            try
            {
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AgentDbContext>();
                if (opts.EnableRollups)
                    await RefreshRollupsAsync(db, stoppingToken).ConfigureAwait(false);
                if (opts.EnablePartitionMaintenance)
                    await EnsureFuturePartitionsAsync(db, opts.PartitionMonthsAhead, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Scale maintenance pass failed");
            }

            await Task.Delay(TimeSpan.FromMinutes(Math.Max(opts.RollupIntervalMinutes, 1)), stoppingToken).ConfigureAwait(false);
        }
    }

    private static async Task RefreshRollupsAsync(AgentDbContext db, CancellationToken ct)
    {
        var since = DateTimeOffset.UtcNow.AddDays(-2);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO agent_ops_hourly_metrics ("Id", "TenantId", "AgentProfile", "HourBucket", "AgentRuns", "ToolCalls", "ToolFailures", "Decisions", "ActionsQueued", "ActionsSucceeded", "ActionsFailed", "LatencyP95Ms", "CostUsd", "UpdatedAt")
            SELECT gen_random_uuid(), d."TenantId", coalesce(nullif(d."AgentProfile", ''), 'unknown'),
                   date_trunc('hour', d."CreatedAt"), 0, 0, 0, count(*), 0, 0, 0, 0, 0, now()
            FROM agent_decisions d
            WHERE d."CreatedAt" >= {since}
            GROUP BY d."TenantId", coalesce(nullif(d."AgentProfile", ''), 'unknown'), date_trunc('hour', d."CreatedAt")
            ON CONFLICT ("TenantId", "HourBucket", "AgentProfile") DO UPDATE
            SET "Decisions" = EXCLUDED."Decisions", "UpdatedAt" = now();
            """, ct).ConfigureAwait(false);

        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO tenant_cost_daily ("Id", "TenantId", "DayBucket", "AgentProfile", "Model", "Runs", "CostUsd", "InputTokens", "OutputTokens", "UpdatedAt")
            SELECT gen_random_uuid(), a."TenantId", date(a."OccurredAt"), 'all', 'all',
                   count(*), 0, 0, 0, now()
            FROM audit_logs a
            WHERE a."OccurredAt" >= {since} AND a."Action" = 'agent.run'
            GROUP BY a."TenantId", date(a."OccurredAt")
            ON CONFLICT ("TenantId", "DayBucket", "AgentProfile", "Model") DO UPDATE
            SET "Runs" = EXCLUDED."Runs", "UpdatedAt" = now();
            """, ct).ConfigureAwait(false);

        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO workflow_success_daily ("Id", "TenantId", "DayBucket", "WorkflowName", "Started", "Succeeded", "Failed", "SuccessRate", "LatencyP95Ms", "UpdatedAt")
            SELECT gen_random_uuid(), a."TenantId", date(a."OccurredAt"),
                   coalesce(nullif(a."ResourceType", ''), 'workflow'),
                   count(*),
                   count(*) FILTER (WHERE a."Action" ILIKE '%completed%' OR a."Action" ILIKE '%succeeded%'),
                   count(*) FILTER (WHERE a."Action" ILIKE '%failed%'),
                   CASE WHEN count(*) = 0 THEN 0 ELSE (count(*) FILTER (WHERE a."Action" ILIKE '%completed%' OR a."Action" ILIKE '%succeeded%'))::float / count(*) END,
                   0,
                   now()
            FROM audit_logs a
            WHERE a."OccurredAt" >= {since} AND a."Action" LIKE 'workflow.%'
            GROUP BY a."TenantId", date(a."OccurredAt"), coalesce(nullif(a."ResourceType", ''), 'workflow')
            ON CONFLICT ("TenantId", "DayBucket", "WorkflowName") DO UPDATE
            SET "Started" = EXCLUDED."Started", "Succeeded" = EXCLUDED."Succeeded", "Failed" = EXCLUDED."Failed",
                "SuccessRate" = EXCLUDED."SuccessRate", "UpdatedAt" = now();
            """, ct).ConfigureAwait(false);
    }

    private static Task EnsureFuturePartitionsAsync(AgentDbContext db, int monthsAhead, CancellationToken ct)
        => db.Database.ExecuteSqlInterpolatedAsync($"SELECT hope_ensure_scale_partitions({Math.Clamp(monthsAhead, 1, 24)});", ct);
}
