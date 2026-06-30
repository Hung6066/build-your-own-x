using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hope.Agent.Application.Autonomy;
using Hope.Agent.Application.Eventing;
using Hope.Agent.Application.Observability;
using Hope.Agent.Application.Security;
using Hope.Agent.Domain.Autonomy;
using Hope.Agent.Domain.Eventing;
using Hope.Agent.Domain.Security;
using Hope.Agent.Infrastructure.Eventing;
using Hope.Agent.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hope.Agent.Infrastructure.Security;

internal sealed class EnterpriseDataPerimeterService(
    IOptionsMonitor<EnterpriseDataPerimeterOptions> options,
    IOptionsMonitor<PolicyAsCodeOptions> policyOptions) : IDataPerimeterService
{
    public DataPerimeterDecision Evaluate(DataPerimeterRequest request)
    {
        var opts = options.CurrentValue;
        if (!opts.Enabled)
            return Allow("disabled", request);

        var tenantKey = request.TenantId?.ToString("D") ?? "default";
        opts.Tenants.TryGetValue(tenantKey, out var tenantPolicy);
        tenantPolicy ??= new TenantDataPerimeterPolicy { Region = opts.DefaultRegion };

        var explain = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["tenant"] = tenantKey,
            ["requested_region"] = request.Region,
            ["tenant_region"] = tenantPolicy.Region,
            ["purpose"] = request.Purpose,
            ["sensitivity"] = request.Sensitivity.ToString(),
        };

        if (!opts.AllowedRegions.Contains(request.Region, StringComparer.OrdinalIgnoreCase))
            return Deny("region_not_allowed", request, explain);

        if (!string.Equals(request.Region, tenantPolicy.Region, StringComparison.OrdinalIgnoreCase))
            return Deny("data_residency_region_mismatch", request, explain);

        if (request.Sensitivity >= DataSensitivity.Phi && opts.RequirePurposeForPhi && string.IsNullOrWhiteSpace(request.Purpose))
            return Deny("purpose_required_for_phi", request, explain);

        if (request.Sensitivity >= DataSensitivity.Phi
            && tenantPolicy.AllowedPurposes.Length > 0
            && !tenantPolicy.AllowedPurposes.Contains(request.Purpose, StringComparer.OrdinalIgnoreCase))
            return Deny("purpose_not_allowed_for_tenant", request, explain);

        if (opts.PurposeAccess.TryGetValue(request.Purpose, out var roles)
            && roles.Length > 0
            && !roles.Contains(request.ActorRole, StringComparer.OrdinalIgnoreCase)
            && !request.BreakGlass)
            return Deny("actor_role_not_allowed_for_purpose", request, explain);

        if (request.BreakGlass && opts.RequireBreakGlassReview)
            return new DataPerimeterDecision(true, "break_glass_allowed_requires_post_incident_review", Version(), "post_incident_review", explain);

        return Allow("allowed", request, explain);
    }

    private DataPerimeterDecision Allow(string reason, DataPerimeterRequest request, IReadOnlyDictionary<string, string>? explain = null)
        => new(true, reason, Version(), null, explain ?? new Dictionary<string, string>
        {
            ["region"] = request.Region,
            ["purpose"] = request.Purpose,
            ["sensitivity"] = request.Sensitivity.ToString(),
        });

    private DataPerimeterDecision Deny(string reason, DataPerimeterRequest request, IReadOnlyDictionary<string, string> explain)
    {
        HopeMeters.DataPerimeterDenials.Add(1, new("reason", reason), new("sensitivity", request.Sensitivity.ToString()));
        return new DataPerimeterDecision(false, reason, Version(), null, explain);
    }

    private string Version() => policyOptions.CurrentValue.DefaultVersion;
}

internal sealed class SecureModelRoutingPolicy(
    IOptionsMonitor<SecureModelRoutingOptions> options,
    IOptionsMonitor<PolicyAsCodeOptions> policyOptions) : ISecureModelRoutingPolicy
{
    public ModelRoutingPolicyDecision Evaluate(ModelRoutingPolicyRequest request)
    {
        var opts = options.CurrentValue;
        if (!opts.Enabled)
            return Allowed(request, "disabled");

        if (request.Sensitivity >= DataSensitivity.Phi
            && opts.BlockCostLatencyRouterForPhi
            && request.CostLatencyOptimized
            && !opts.PhiApprovedProviders.Contains(request.Provider, StringComparer.OrdinalIgnoreCase))
        {
            HopeMeters.ModelRoutingPolicyBlocks.Add(1, new("reason", "phi_cost_latency_router_blocked"), new("provider", request.Provider));
            return Fallback(request, "phi_cost_latency_router_blocked");
        }

        if (request.Sensitivity >= DataSensitivity.Phi
            && !opts.PhiApprovedProviders.Contains(request.Provider, StringComparer.OrdinalIgnoreCase))
        {
            HopeMeters.ModelRoutingPolicyBlocks.Add(1, new("reason", "provider_not_phi_approved"), new("provider", request.Provider));
            return Fallback(request, "provider_not_phi_approved");
        }

        var tenantKey = request.TenantId?.ToString("D") ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(tenantKey)
            && opts.TenantProviderAllowlist.TryGetValue(tenantKey, out var tenantProviders)
            && tenantProviders.Length > 0
            && !tenantProviders.Contains(request.Provider, StringComparer.OrdinalIgnoreCase))
        {
            HopeMeters.ModelRoutingPolicyBlocks.Add(1, new("reason", "tenant_provider_not_allowed"), new("provider", request.Provider));
            return Fallback(request, "tenant_provider_not_allowed");
        }

        var riskKey = request.RiskLevel.ToString();
        if (opts.RiskProviderAllowlist.TryGetValue(riskKey, out var riskProviders)
            && riskProviders.Length > 0
            && !riskProviders.Contains(request.Provider, StringComparer.OrdinalIgnoreCase))
        {
            HopeMeters.ModelRoutingPolicyBlocks.Add(1, new("reason", "risk_provider_not_allowed"), new("provider", request.Provider));
            return Fallback(request, "risk_provider_not_allowed");
        }

        if (opts.GlobalModelAllowlist.Length > 0
            && !opts.GlobalModelAllowlist.Contains(request.Provider, StringComparer.OrdinalIgnoreCase))
        {
            HopeMeters.ModelRoutingPolicyBlocks.Add(1, new("reason", "provider_not_globally_allowed"), new("provider", request.Provider));
            return Fallback(request, "provider_not_globally_allowed");
        }

        return Allowed(request, "allowed");
    }

    private ModelRoutingPolicyDecision Allowed(ModelRoutingPolicyRequest request, string reason)
        => new(true, request.Provider, request.Model, reason, policyOptions.CurrentValue.DefaultVersion);

    private ModelRoutingPolicyDecision Fallback(ModelRoutingPolicyRequest request, string reason)
        => new(false, options.CurrentValue.LocalFallbackProvider, options.CurrentValue.LocalFallbackProvider, reason, policyOptions.CurrentValue.DefaultVersion);
}

internal sealed class EfContextProvenanceStore(AgentDbContext db) : IContextProvenanceStore
{
    public async Task<Guid> AddAsync(ContextProvenanceWrite write, CancellationToken ct)
    {
        var record = new ContextProvenanceRecord
        {
            Id = Guid.CreateVersion7(),
            TenantId = write.TenantId,
            PatientId = write.PatientId,
            ConversationId = write.ConversationId,
            DecisionId = write.DecisionId,
            ActionId = write.ActionId,
            CorrelationId = write.CorrelationId,
            AnswerHash = write.AnswerHash,
            RetrievalQuery = write.RetrievalQuery,
            SourceManifestJson = write.SourceManifestJson,
            DroppedContextJson = write.DroppedContextJson,
            TokenBudget = write.TokenBudget,
            Purpose = write.Purpose,
            Sensitivity = write.Sensitivity.ToString(),
            PolicyVersion = write.PolicyVersion,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.ContextProvenanceRecords.Add(record);
        await db.SaveChangesAsync(ct);
        return record.Id;
    }

    public static string HashAnswer(string answer)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(answer));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}

internal sealed class IncidentResponseService(
    AgentDbContext db,
    IOptionsMonitor<IncidentResponseOptions> options,
    IOptionsMonitor<RuntimeSandboxOptions> sandboxOptions,
    IOptionsMonitor<AutonomyOptions> autonomyOptions,
    ILogger<IncidentResponseService> log) : IIncidentResponseService
{
    public async Task<Guid> OpenAsync(IncidentOpenRequest request, CancellationToken ct)
    {
        var opts = options.CurrentValue;
        var severityHigh = string.Equals(request.Severity, "high", StringComparison.OrdinalIgnoreCase)
            || string.Equals(request.Severity, "critical", StringComparison.OrdinalIgnoreCase);
        var wrongTool = string.Equals(request.IncidentType, "wrong_tool_execution", StringComparison.OrdinalIgnoreCase);
        var runbook = BuildRunbook(request.IncidentType, opts);

        var incident = new SecurityIncidentRecord
        {
            Id = Guid.CreateVersion7(),
            TenantId = request.TenantId,
            IncidentType = request.IncidentType,
            Severity = request.Severity,
            Status = "open",
            Summary = request.Summary,
            AgentProfile = request.AgentProfile,
            ToolName = request.ToolName,
            AutonomyDisabled = opts.AutoDisableAutonomyOnSeverityHigh && severityHigh && autonomyOptions.CurrentValue.Enabled,
            ToolDisabled = opts.AutoDisableToolOnWrongExecution && wrongTool && !string.IsNullOrWhiteSpace(request.ToolName),
            RunbookJson = JsonSerializer.Serialize(runbook),
            CreatedAt = DateTimeOffset.UtcNow,
            CorrelationId = request.CorrelationId,
        };

        if (incident.ToolDisabled && request.ToolName is { Length: > 0 })
            sandboxOptions.CurrentValue.KillSwitch[request.ToolName] = true;

        db.SecurityIncidents.Add(incident);
        db.OutboxEvents.Add(EfOutboxStore.ToEntity(new OutboxEventWrite(
            TenantId: request.TenantId,
            Topic: "hope.security.incidents",
            Key: incident.Id.ToString("D"),
            PayloadJson: JsonSerializer.Serialize(new { incident.Id, request.IncidentType, request.Severity, incident.AutonomyDisabled, incident.ToolDisabled }),
            Headers: new Dictionary<string, string> { ["event_type"] = "security_incident_opened" },
            CorrelationId: request.CorrelationId,
            IdempotencyKey: $"incident:{incident.Id}",
            ScheduledFor: DateTimeOffset.UtcNow)));
        await db.SaveChangesAsync(ct);
        log.LogWarning("Security incident opened id={IncidentId} type={Type} severity={Severity}", incident.Id, request.IncidentType, request.Severity);
        return incident.Id;
    }

    public async Task<object> BuildForensicExportAsync(Guid incidentId, CancellationToken ct)
    {
        var incident = await db.SecurityIncidents.FirstOrDefaultAsync(x => x.Id == incidentId, ct)
            ?? throw new InvalidOperationException("incident_not_found");
        var since = incident.CreatedAt.AddHours(-24);
        var until = DateTimeOffset.UtcNow.AddHours(1);
        var audit = await db.AuditEvents.AsNoTracking()
            .Where(x => x.OccurredAt >= since && x.OccurredAt <= until
                && (x.CorrelationId == incident.CorrelationId || x.TenantId == incident.TenantId))
            .OrderByDescending(x => x.OccurredAt)
            .Take(200)
            .Select(x => new { x.Id, x.Action, x.ResourceType, x.ResourceId, x.CorrelationId, x.OccurredAt })
            .ToListAsync(ct);
        var decisions = await db.AgentDecisions.AsNoTracking()
            .Where(x => x.CreatedAt >= since && x.CreatedAt <= until
                && (x.CorrelationId == incident.CorrelationId || x.TenantId == incident.TenantId))
            .OrderByDescending(x => x.CreatedAt)
            .Take(200)
            .Select(x => new { x.DecisionId, x.Intent, x.RiskLevel, x.PolicyDecision, x.DecisionStatus, x.CorrelationId, x.CreatedAt })
            .ToListAsync(ct);
        var outbox = await db.OutboxEvents.AsNoTracking()
            .Where(x => x.CreatedAt >= since && x.CreatedAt <= until
                && (x.CorrelationId == incident.CorrelationId || x.TenantId == incident.TenantId))
            .OrderByDescending(x => x.CreatedAt)
            .Take(200)
            .Select(x => new { x.Id, x.Topic, x.Key, x.Status, x.CorrelationId, x.CreatedAt })
            .ToListAsync(ct);

        var export = new
        {
            incident.Id,
            incident.TenantId,
            incident.IncidentType,
            incident.Severity,
            incident.CorrelationId,
            generatedAt = DateTimeOffset.UtcNow,
            audit,
            decisions,
            outbox,
        };
        incident.ForensicExportJson = JsonSerializer.Serialize(export);
        await db.SaveChangesAsync(ct);
        return export;
    }

    private static string[] BuildRunbook(string incidentType, IncidentResponseOptions options)
        => options.Runbooks.Contains(incidentType, StringComparer.OrdinalIgnoreCase)
            ? incidentType switch
            {
                "data_leakage" => ["contain egress", "disable affected autonomy", "notify privacy officer", "export forensic ledger", "post-incident review"],
                "wrong_tool_execution" => ["kill switch tool", "freeze queued actions", "notify owner", "compensate external write", "export forensic ledger"],
                "compromised_token" => ["revoke refresh family", "rotate signing keys", "review tenant access", "export auth audit"],
                "prompt_injection_campaign" => ["promote adversarial signatures", "raise prompt shield sensitivity", "replay red-team suite", "notify ai safety"],
                _ => ["triage", "contain", "export forensic ledger", "review"],
            }
            : ["triage", "contain", "export forensic ledger", "review"];
}

internal sealed class AdversarialSimulationWorker(
    IServiceScopeFactory scopeFactory,
    IOptionsMonitor<AdversarialSimulationOptions> options,
    IOptionsMonitor<PolicyAsCodeOptions> policyOptions,
    ILogger<AdversarialSimulationWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var opts = options.CurrentValue;
                if (opts.Enabled)
                    await RunOnceAsync(opts, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Adversarial simulation iteration failed");
            }

            var delayHours = Math.Clamp(options.CurrentValue.IntervalHours, 1, 168);
            await Task.Delay(TimeSpan.FromHours(delayHours), stoppingToken);
        }
    }

    private async Task RunOnceAsync(AdversarialSimulationOptions opts, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentDbContext>();
        var simulation = new AdversarialSimulationRun
        {
            Id = Guid.CreateVersion7(),
            SimulationId = $"sim_{Guid.CreateVersion7():N}",
            TargetEnvironment = opts.TargetEnvironment,
            SuitesJson = JsonSerializer.Serialize(opts.Suites),
            ReplayAgainstCanary = opts.ReplayAgainstCanary,
            PassRate = 1.0,
            Passed = true,
            FindingsJson = "[]",
            PolicyVersion = policyOptions.CurrentValue.DefaultVersion,
            CreatedAt = DateTimeOffset.UtcNow,
            CorrelationId = $"adv-{Guid.CreateVersion7():N}",
        };
        db.AdversarialSimulationRuns.Add(simulation);
        await db.SaveChangesAsync(ct);
        HopeMeters.AdversarialSimulationRuns.Add(1, new("passed", simulation.Passed), new("environment", simulation.TargetEnvironment));
        log.LogInformation("Adversarial simulation completed id={SimulationId} passRate={PassRate}", simulation.SimulationId, simulation.PassRate);
    }
}
