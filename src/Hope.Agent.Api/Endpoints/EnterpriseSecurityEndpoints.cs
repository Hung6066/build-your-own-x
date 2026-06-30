using System.Security.Claims;
using System.Text.Json;
using Hope.Agent.Application.Observability;
using Hope.Agent.Application.Governance;
using Hope.Agent.Application.Security;
using Hope.Agent.Domain.Autonomy;
using Hope.Agent.Domain.Security;
using Hope.Agent.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Hope.Agent.Api.Endpoints;

public static class EnterpriseSecurityEndpoints
{
    public static IEndpointRouteBuilder MapEnterpriseSecurityEndpoints(this IEndpointRouteBuilder app)
    {
        var grp = app.MapGroup("/v1/security/enterprise")
            .RequireAuthorization("TenantAccess")
            .WithTags("Enterprise Security");

        grp.MapPost("/data-perimeter/evaluate", (
            [FromBody] DataPerimeterEvaluateRequest req,
            [FromServices] IDataPerimeterService perimeter) =>
        {
            var decision = perimeter.Evaluate(new DataPerimeterRequest(
                req.TenantId,
                req.Purpose,
                ParseSensitivity(req.Sensitivity),
                req.Region,
                req.ActorRole,
                req.BreakGlass));
            return Results.Ok(decision);
        }).WithSummary("Evaluate region-aware, classification-aware, purpose-based access policy.");

        grp.MapPost("/model-routing/evaluate", (
            [FromBody] ModelRoutingEvaluateRequest req,
            [FromServices] ISecureModelRoutingPolicy routing) =>
        {
            var decision = routing.Evaluate(new ModelRoutingPolicyRequest(
                req.TenantId,
                req.Intent,
                req.Provider,
                req.Model,
                Enum.TryParse<AutonomyRiskLevel>(req.RiskLevel, true, out var risk) ? risk : AutonomyRiskLevel.High,
                ParseSensitivity(req.Sensitivity),
                req.CostLatencyOptimized));
            return Results.Ok(decision);
        }).WithSummary("Evaluate secure model routing before cost/latency optimization can select a provider.");

        grp.MapPost("/break-glass", async (
            ClaimsPrincipal user,
            [FromBody] BreakGlassRequest req,
            [FromServices] AgentDbContext db,
            [FromServices] IOptionsMonitor<EnterpriseDataPerimeterOptions> options,
            CancellationToken ct) =>
        {
            var tenantId = req.TenantId == Guid.Empty ? SecurityDefaults.DefaultTenantId : req.TenantId;
            var record = new BreakGlassAccessRecord
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                ActorUserId = ResolveUserId(user),
                Purpose = req.Purpose,
                Reason = req.Reason,
                Status = "pending_post_review",
                CreatedAt = DateTimeOffset.UtcNow,
                ReviewDueAt = DateTimeOffset.UtcNow.AddHours(Math.Clamp(options.CurrentValue.BreakGlassReviewDueHours, 1, 168)),
                CorrelationId = req.CorrelationId,
            };
            db.BreakGlassAccessRecords.Add(record);
            await db.SaveChangesAsync(ct);
            HopeMeters.BreakGlassAccesses.Add(1, new KeyValuePair<string, object?>("purpose", req.Purpose));
            return Results.Ok(new { record.Id, record.Status, record.ReviewDueAt });
        }).WithSummary("Open break-glass access with mandatory post-incident review.");

        grp.MapGet("/provenance", async (
            [FromServices] AgentDbContext db,
            [FromQuery] Guid? tenantId,
            [FromQuery] Guid? patientId,
            [FromQuery] string? decisionId,
            [FromQuery] string? correlationId,
            [FromQuery] int take,
            CancellationToken ct) =>
        {
            var query = db.ContextProvenanceRecords.AsNoTracking();
            if (tenantId is { } t) query = query.Where(x => x.TenantId == t);
            if (patientId is { } p) query = query.Where(x => x.PatientId == p);
            if (!string.IsNullOrWhiteSpace(decisionId)) query = query.Where(x => x.DecisionId == decisionId);
            if (!string.IsNullOrWhiteSpace(correlationId)) query = query.Where(x => x.CorrelationId == correlationId);

            var records = await query.OrderByDescending(x => x.CreatedAt)
                .Take(Math.Clamp(take == 0 ? 20 : take, 1, 100))
                .ToListAsync(ct);

            var rows = records.Select(x => new
            {
                x.Id,
                x.TenantId,
                x.PatientId,
                x.ConversationId,
                x.DecisionId,
                x.ActionId,
                x.CorrelationId,
                x.AnswerHash,
                x.RetrievalQuery,
                x.TokenBudget,
                x.Purpose,
                x.Sensitivity,
                x.PolicyVersion,
                x.CreatedAt,
                SourceManifest = JsonElementOrNull(x.SourceManifestJson),
                DroppedContext = JsonElementOrNull(x.DroppedContextJson),
            });
            return Results.Ok(rows);
        }).WithSummary("Fine-grained answer/action provenance for security and audit teams.");

        grp.MapPost("/incidents", async (
            [FromBody] OpenIncidentRequest req,
            [FromServices] IIncidentResponseService incidents,
            CancellationToken ct) =>
        {
            var id = await incidents.OpenAsync(new IncidentOpenRequest(
                req.TenantId,
                req.IncidentType,
                req.Severity,
                req.Summary,
                req.CorrelationId,
                req.AgentProfile,
                req.ToolName), ct);
            HopeMeters.SecurityIncidentsOpened.Add(1, new("type", req.IncidentType), new("severity", req.Severity));
            return Results.Ok(new { incidentId = id });
        }).WithSummary("Open a formal security incident and trigger configured containment flags.");

        grp.MapGet("/incidents/{id:guid}/forensics", async (
            Guid id,
            [FromServices] IIncidentResponseService incidents,
            CancellationToken ct) =>
        {
            var export = await incidents.BuildForensicExportAsync(id, ct);
            return Results.Ok(export);
        }).WithSummary("Export forensic bundle from audit, outbox, and decision ledgers.");

        grp.MapGet("/adversarial-simulations", async (
            [FromServices] AgentDbContext db,
            [FromQuery] int take,
            CancellationToken ct) =>
        {
            var rows = await db.AdversarialSimulationRuns.AsNoTracking()
                .OrderByDescending(x => x.CreatedAt)
                .Take(Math.Clamp(take == 0 ? 20 : take, 1, 100))
                .ToListAsync(ct);
            return Results.Ok(rows);
        }).WithSummary("Continuous red-team simulation ledger.");

        grp.MapGet("/posture/encryption", async (
            [FromServices] AgentDbContext db,
            [FromServices] IConfiguration configuration,
            [FromServices] IOptionsMonitor<StorageEncryptionOptions> storage,
            [FromServices] IOptionsMonitor<AuditImmutabilityOptions> audit,
            [FromServices] IOptionsMonitor<SecretManagementOptions> secrets,
            [FromServices] IOptionsMonitor<DatabaseScaleOptions> databaseScale,
            [FromServices] IOptionsMonitor<RuntimeScaleOptions> runtimeScale,
            CancellationToken ct) =>
        {
            var replicaName = string.IsNullOrWhiteSpace(runtimeScale.CurrentValue.ReadReplicaConnectionName)
                ? "PostgresReadReplica"
                : runtimeScale.CurrentValue.ReadReplicaConnectionName;
            var checks = await ReadEncryptionPostureChecksAsync(db, ct);

            return Results.Ok(new
            {
                storageEncryption = new
                {
                    storage.CurrentValue.RequireAtRestEncryption,
                    storage.CurrentValue.AtRestEnabled,
                    storage.CurrentValue.Provider,
                },
                envelopeEncryption = new
                {
                    secrets.CurrentValue.RequireKmsEnvelopeEncryption,
                    hasKmsKeyId = !string.IsNullOrWhiteSpace(secrets.CurrentValue.KmsKeyId),
                },
                auditImmutability = new
                {
                    audit.CurrentValue.Enabled,
                    audit.CurrentValue.RequireWormArchive,
                    audit.CurrentValue.WormArchiveUri,
                },
                readReplica = new
                {
                    databaseScale.CurrentValue.PreferReadReplicaForDashboard,
                    connectionName = replicaName,
                    configured = !string.IsNullOrWhiteSpace(configuration.GetConnectionString(replicaName)),
                },
                securityPostureChecks = checks,
            });
        }).WithSummary("Expose at-rest/envelope encryption and WORM evidence posture for runtime verification.");

        return app;
    }

    private static Guid ResolveUserId(ClaimsPrincipal user)
        => Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue("sub"), out var id)
            ? id
            : Guid.Empty;

    private static DataSensitivity ParseSensitivity(string? value)
        => Enum.TryParse<DataSensitivity>(value, true, out var parsed) ? parsed : DataSensitivity.Phi;

    private static JsonElement? JsonElementOrNull(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            return doc.RootElement.Clone();
        }
        catch
        {
            return null;
        }
    }

    private static async Task<IReadOnlyList<object>> ReadEncryptionPostureChecksAsync(AgentDbContext db, CancellationToken ct)
    {
        try
        {
            await db.Database.OpenConnectionAsync(ct);
            await using var cmd = db.Database.GetDbConnection().CreateCommand();
            cmd.CommandText = """
                SELECT "CheckName", "RequiredState", "CurrentState", "Severity", "UpdatedAt"
                FROM security_posture_checks
                WHERE "CheckName" IN ('kms_envelope_encryption', 'audit_worm')
                ORDER BY "CheckName"
                """;

            var rows = new List<object>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                rows.Add(new
                {
                    checkName = reader.GetString(0),
                    requiredState = reader.GetString(1),
                    currentState = reader.GetString(2),
                    severity = reader.GetString(3),
                    updatedAt = reader.GetDateTime(4),
                });
            }

            return rows;
        }
        catch
        {
            return [
                new
                {
                    checkName = "posture_checks_unavailable",
                    requiredState = "migration security_posture_checks expected",
                    currentState = "unavailable",
                    severity = "warning",
                    updatedAt = DateTime.UtcNow,
                },
            ];
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }
}

public sealed record DataPerimeterEvaluateRequest(
    Guid? TenantId,
    string Purpose,
    string Sensitivity,
    string Region,
    string ActorRole,
    bool BreakGlass = false);

public sealed record ModelRoutingEvaluateRequest(
    Guid? TenantId,
    string Intent,
    string Provider,
    string Model,
    string RiskLevel,
    string Sensitivity,
    bool CostLatencyOptimized = true);

public sealed record BreakGlassRequest(
    Guid TenantId,
    string Purpose,
    string Reason,
    string? CorrelationId = null);

public sealed record OpenIncidentRequest(
    Guid? TenantId,
    string IncidentType,
    string Severity,
    string Summary,
    string? CorrelationId = null,
    string? AgentProfile = null,
    string? ToolName = null);
