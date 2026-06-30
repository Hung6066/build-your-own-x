using Hope.Agent.Application.Governance;
using Hope.Agent.Application.Learning;
using Hope.Agent.Application.Observability;
using Hope.Agent.Application.Security;
using Hope.Agent.Infrastructure.Persistence;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Hope.Agent.Api.Endpoints;

public static class HarnessEndpoints
{
    public static IEndpointRouteBuilder MapHarnessEndpoints(this IEndpointRouteBuilder app)
    {
        var grp = app.MapGroup("/v1/harness")
            .RequireAuthorization()
            .WithTags("Harness");

        grp.MapGet("/status", async (
            [FromServices] AgentDbContext db,
            [FromServices] IEvaluationHarness eval,
            [FromServices] IOptionsMonitor<ToolApprovalOptions> toolApproval,
            [FromServices] IOptionsMonitor<AgentOwnershipOptions> ownership,
            [FromServices] IOptionsMonitor<AccessMatrixOptions> accessMatrix,
            [FromServices] IOptionsMonitor<ApprovalSlaOptions> approvalSla,
            [FromServices] IOptionsMonitor<AgentOpsOptions> agentOps,
            [FromServices] IOptionsMonitor<OrchestrationDagOptions> dags,
            [FromServices] IOptionsMonitor<AgentRegistryOptions> registry,
            [FromServices] IOptionsMonitor<EnterpriseDataPerimeterOptions> enterprisePerimeter,
            [FromServices] IOptionsMonitor<SecureModelRoutingOptions> secureRouting,
            [FromServices] IOptionsMonitor<AdversarialSimulationOptions> adversarialSimulation,
            [FromServices] IOptionsMonitor<IncidentResponseOptions> incidentResponse,
            [FromQuery] string? suite,
            CancellationToken ct) =>
        {
            var since = DateTimeOffset.UtcNow.AddDays(-1);
            var evalMetrics = await eval.GetMetricsAsync(suite ?? "default", 30, ct);
            var decisions = await db.AgentDecisions.AsNoTracking().CountAsync(x => x.CreatedAt >= since, ct);
            var actions = await db.AutonomousActions.AsNoTracking().CountAsync(x => x.CreatedAt >= since, ct);
            var auditEvents = await db.AuditEvents.AsNoTracking().CountAsync(x => x.OccurredAt >= since, ct);
            var memories = await db.Memories.AsNoTracking().CountAsync(ct);
            var docs = await db.Documents.AsNoTracking().CountAsync(ct);
            var approvals = await db.ToolApprovalRequests.AsNoTracking().CountAsync(ct);
            var provenance = await db.ContextProvenanceRecords.AsNoTracking().CountAsync(ct);
            var incidents = await db.SecurityIncidents.AsNoTracking().CountAsync(x => x.Status == "open", ct);
            var simulations = await db.AdversarialSimulationRuns.AsNoTracking().CountAsync(x => x.CreatedAt >= since, ct);
            var latestGate = await db.AutonomyEvalGateRuns.AsNoTracking()
                .OrderByDescending(x => x.CreatedAt)
                .Select(x => new { x.GateId, x.Passed, x.PassRate, x.CreatedAt })
                .FirstOrDefaultAsync(ct);
            var latestDrift = await db.AutonomyDriftSignals.AsNoTracking()
                .OrderByDescending(x => x.CreatedAt)
                .Select(x => new { x.SignalId, x.Severity, x.Score, x.CreatedAt })
                .FirstOrDefaultAsync(ct);

            return Results.Ok(new
            {
                contextHarness = new
                {
                    enabled = true,
                    memoryRecords = memories,
                    ragDocuments = docs,
                    provenance = "agent.run audit payload includes contextManifest",
                },
                toolHarness = new
                {
                    toolApproval.CurrentValue.Enabled,
                    toolApproval.CurrentValue.AllowUnconfiguredToolAccess,
                    configuredTools = toolApproval.CurrentValue.Tools.Keys.Order().ToArray(),
                    configuredRoleAccess = toolApproval.CurrentValue.ToolRoleAccess.Keys.Order().ToArray(),
                },
                orchestrationHarness = new
                {
                    dags = dags.CurrentValue.Workflows,
                    autonomousDecisionsLast24h = decisions,
                    autonomousActionsLast24h = actions,
                },
                evaluationHarness = evalMetrics,
                securityHarness = new
                {
                    promptShield = true,
                    outputShield = true,
                    phiRedaction = true,
                    retrievalRail = true,
                    toolRbac = true,
                    humanApproval = toolApproval.CurrentValue.Enabled,
                    dataPerimeter = enterprisePerimeter.CurrentValue.Enabled,
                    secureModelRouting = secureRouting.CurrentValue.Enabled,
                    adversarialSimulation = adversarialSimulation.CurrentValue.Enabled,
                    incidentResponse = incidentResponse.CurrentValue.Enabled,
                    provenanceRecords = provenance,
                    openSecurityIncidents = incidents,
                    adversarialSimulationsLast24h = simulations,
                },
                governanceHarness = new
                {
                    ownership.CurrentValue.DefaultResponsibleRole,
                    ownership.CurrentValue.DefaultApproverRole,
                    agentPolicies = ownership.CurrentValue.Agents,
                    accessMatrix = accessMatrix.CurrentValue,
                    approvalSla = approvalSla.CurrentValue,
                    agentRegistry = registry.CurrentValue,
                    approvalsPersisted = approvals,
                    auditEventsLast24h = auditEvents,
                    latestGate,
                    latestDrift,
                },
                agentOpsHarness = new
                {
                    meter = HopeMeters.MeterName,
                    alertChannel = agentOps.CurrentValue.AlertChannel,
                    alertRules = agentOps.CurrentValue.AlertRules,
                    versioning = "agent.run audit payload includes versionFingerprint",
                },
            });
        });

        grp.MapGet("/governance", (
            [FromServices] IOptionsMonitor<AgentOwnershipOptions> ownership,
            [FromServices] IOptionsMonitor<AccessMatrixOptions> accessMatrix,
            [FromServices] IOptionsMonitor<ApprovalSlaOptions> approvalSla,
            [FromServices] IOptionsMonitor<AgentOpsOptions> agentOps,
            [FromServices] IOptionsMonitor<OrchestrationDagOptions> dags,
            [FromServices] IOptionsMonitor<AgentRegistryOptions> registry) =>
        {
            return Results.Ok(new
            {
                ownership = ownership.CurrentValue,
                accessMatrix = accessMatrix.CurrentValue,
                approvalSla = approvalSla.CurrentValue,
                agentOps = agentOps.CurrentValue,
                orchestrationDags = dags.CurrentValue,
                agentRegistry = registry.CurrentValue,
            });
        });

        grp.MapGet("/context-provenance", async (
            [FromServices] AgentDbContext db,
            [FromQuery] Guid? conversationId,
            [FromQuery] string? correlationId,
            [FromQuery] int take,
            CancellationToken ct) =>
        {
            var query = db.AuditEvents.AsNoTracking()
                .Where(x => x.Action == "agent.run");
            if (conversationId is { } cid)
                query = query.Where(x => x.ResourceId == cid.ToString());
            if (!string.IsNullOrWhiteSpace(correlationId))
                query = query.Where(x => x.CorrelationId == correlationId);

            var rows = await query.OrderByDescending(x => x.OccurredAt)
                .Take(Math.Clamp(take == 0 ? 20 : take, 1, 100))
                .ToListAsync(ct);

            var result = rows.Select(row =>
            {
                JsonElement? context = null;
                JsonElement? version = null;
                JsonElement? tools = null;
                try
                {
                    using var doc = JsonDocument.Parse(row.PayloadJson ?? "{}");
                    if (doc.RootElement.TryGetProperty("contextManifest", out var c)) context = c.Clone();
                    if (doc.RootElement.TryGetProperty("versionFingerprint", out var v)) version = v.Clone();
                    if (doc.RootElement.TryGetProperty("tools", out var t)) tools = t.Clone();
                }
                catch { }

                return new
                {
                    row.Id,
                    row.OccurredAt,
                    ConversationId = row.ResourceId,
                    row.CorrelationId,
                    ContextManifest = context,
                    VersionFingerprint = version,
                    Tools = tools,
                };
            });
            return Results.Ok(result);
        }).WithSummary("Debug what context sources and versions were injected for agent.run calls.");

        grp.MapGet("/workflows/debug/{workflow}", async (
            string workflow,
            [FromServices] AgentDbContext db,
            [FromServices] IOptionsMonitor<OrchestrationDagOptions> dags,
            [FromQuery] int take,
            CancellationToken ct) =>
        {
            dags.CurrentValue.Workflows.TryGetValue(workflow, out var dag);
            var since = DateTimeOffset.UtcNow.AddDays(-7);
            var decisions = await db.AgentDecisions.AsNoTracking()
                .Where(x => x.CreatedAt >= since && (x.Intent == workflow || x.AgentProfile == workflow || (x.CorrelationId != null && x.CorrelationId.Contains(workflow))))
                .OrderByDescending(x => x.CreatedAt)
                .Take(Math.Clamp(take == 0 ? 20 : take, 1, 100))
                .Select(x => new { x.DecisionId, x.Intent, x.RiskLevel, x.PolicyDecision, x.DecisionStatus, x.Reason, x.CreatedAt, x.CorrelationId })
                .ToListAsync(ct);
            var mermaid = dag is null
                ? null
                : "flowchart TD\n" + string.Join("\n", dag.Edges.Select(edge => "    " + edge.Replace("->", " --> ")));
            return Results.Ok(new { workflow, dag, mermaid, recentDecisions = decisions });
        }).WithSummary("Workflow replay/debug view: DAG spec, Mermaid graph, and recent decision trace.");

        return app;
    }
}
