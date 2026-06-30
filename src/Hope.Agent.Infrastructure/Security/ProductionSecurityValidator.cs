using Hope.Agent.Application.Security;
using Hope.Agent.Application.Governance;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hope.Agent.Infrastructure.Security;

internal sealed class ProductionSecurityValidator(
    IHostEnvironment environment,
    IConfiguration configuration,
    IOptionsMonitor<ZeroTrustOptions> zeroTrust,
    IOptionsMonitor<SecretManagementOptions> secrets,
    IOptionsMonitor<DataPerimeterOptions> dataPerimeter,
    IOptionsMonitor<StorageEncryptionOptions> storageEncryption,
    IOptionsMonitor<RedisHighAvailabilityOptions> redisHa,
    IOptionsMonitor<DlpOptions> dlp,
    IOptionsMonitor<EgressPolicyOptions> egress,
    IOptionsMonitor<RuntimeSandboxOptions> sandbox,
    IOptionsMonitor<ToolApprovalOptions> tools,
    IOptionsMonitor<RuntimeScaleOptions> runtimeScale,
    IOptionsMonitor<DatabaseScaleOptions> dbScale,
    ILogger<ProductionSecurityValidator> log) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!environment.IsProduction())
        {
            log.LogInformation("Production security validator skipped outside Production.");
            return Task.CompletedTask;
        }

        var errors = new List<string>();
        var zt = zeroTrust.CurrentValue;
        if (zt.RequireMtls && zt.RequiredMtlsServices.Length == 0) errors.Add("ZeroTrust:RequiredMtlsServices must not be empty.");
        if (zt.RequireWorkloadIdentity && string.IsNullOrWhiteSpace(zt.WorkloadIdentityProvider)) errors.Add("ZeroTrust:WorkloadIdentityProvider is required.");
        if (zt.RequireShortLivedInternalJwt && zt.InternalJwtMaxLifetimeMinutes > 15) errors.Add("ZeroTrust:InternalJwtMaxLifetimeMinutes must be <= 15.");

        var sec = secrets.CurrentValue;
        if (sec.RequireExternalSecretProvider && !configuration.GetValue<bool>("KeyVault:Enabled")) errors.Add("KeyVault must be enabled in Production.");
        if (sec.RequireKmsEnvelopeEncryption && string.IsNullOrWhiteSpace(sec.KmsKeyId)) errors.Add("Secrets:KmsKeyId is required for envelope encryption.");
        if (sec.RejectInlineProductionSecrets)
        {
            RejectInlineSecret(configuration["Jwt:Secret"], "Jwt:Secret", errors);
            RejectInlineSecret(configuration["Channels:Slack:BotToken"], "Channels:Slack:BotToken", errors);
            RejectInlineSecret(configuration["Channels:Zalo:OaAccessToken"], "Channels:Zalo:OaAccessToken", errors);
        }

        var dp = dataPerimeter.CurrentValue;
        if (!dp.RequirePostgresRls) errors.Add("DataPerimeter:RequirePostgresRls must be true.");
        if (!dp.RequireTenantIdNotNull) errors.Add("DataPerimeter:RequireTenantIdNotNull must be true.");
        if (dp.RequireRedisKeyNamespace && string.IsNullOrWhiteSpace(dp.RedisKeyPrefix)) errors.Add("DataPerimeter:RedisKeyPrefix is required.");
        if (!dp.RequireQdrantTenantPayloadFilter) errors.Add("DataPerimeter:RequireQdrantTenantPayloadFilter must be true.");

        var se = storageEncryption.CurrentValue;
        if (se.RequireAtRestEncryption && !se.AtRestEnabled)
            errors.Add("StorageEncryption:AtRestEnabled must be true in Production.");

        var pg = configuration.GetConnectionString("Postgres") ?? string.Empty;
        if (!pg.Contains("SSL Mode=Require", StringComparison.OrdinalIgnoreCase)
            && !pg.Contains("SSL Mode=VerifyFull", StringComparison.OrdinalIgnoreCase))
            errors.Add("ConnectionStrings:Postgres must enforce SSL Mode=Require or VerifyFull in Production.");

        var redisConn = configuration.GetConnectionString("Redis") ?? string.Empty;
        var redisTls = redisConn.StartsWith("rediss://", StringComparison.OrdinalIgnoreCase)
            || redisConn.Contains("ssl=true", StringComparison.OrdinalIgnoreCase)
            || redisConn.Contains("ssl = true", StringComparison.OrdinalIgnoreCase);
        if (!redisTls && !redisHa.CurrentValue.Enabled)
            errors.Add("ConnectionStrings:Redis must enforce TLS (rediss:// or ssl=true) unless RedisHa sentinel mode is enabled.");
        if (redisHa.CurrentValue.Enabled)
        {
            if (redisHa.CurrentValue.Endpoints.Length == 0)
                errors.Add("RedisHa:Endpoints must not be empty when RedisHa:Enabled=true.");
            if (string.IsNullOrWhiteSpace(redisHa.CurrentValue.ServiceName))
                errors.Add("RedisHa:ServiceName is required when RedisHa:Enabled=true.");
        }

        var qdrantHost = configuration["Qdrant:Host"] ?? string.Empty;
        if (!qdrantHost.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            errors.Add("Qdrant:Host must use https:// in Production.");

        var temporalHosts = configuration.GetSection("Temporal:TargetHosts").Get<string[]>() ?? [];
        if (temporalHosts.Length < 2)
            errors.Add("Temporal:TargetHosts must contain at least 2 hosts for HA in Production.");

        var scale = dbScale.CurrentValue;
        if (scale.PreferReadReplicaForDashboard)
        {
            var replicaName = runtimeScale.CurrentValue.ReadReplicaConnectionName;
            if (string.IsNullOrWhiteSpace(replicaName))
                replicaName = "PostgresReadReplica";
            var replicaConn = configuration.GetConnectionString(replicaName) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(replicaConn))
                errors.Add($"ConnectionStrings:{replicaName} is required when DatabaseScale:PreferReadReplicaForDashboard=true.");
            else if (!replicaConn.Contains("SSL Mode=Require", StringComparison.OrdinalIgnoreCase)
                     && !replicaConn.Contains("SSL Mode=VerifyFull", StringComparison.OrdinalIgnoreCase))
                errors.Add($"ConnectionStrings:{replicaName} must enforce SSL Mode=Require or VerifyFull in Production.");
        }

        var tool = tools.CurrentValue;
        if (!tool.Enabled) errors.Add("ToolApproval must be enabled.");
        if (tool.AllowUnconfiguredToolAccess) errors.Add("ToolApproval:AllowUnconfiguredToolAccess must be false.");
        if (!tool.RequireIdempotencyKeyForWrites) errors.Add("ToolApproval:RequireIdempotencyKeyForWrites must be true.");

        if (!dlp.CurrentValue.Enabled || !dlp.CurrentValue.RedactPhiOnExternalChannels)
            errors.Add("Dlp must be enabled and redact PHI on external channels.");

        if (!egress.CurrentValue.RequireAllowlist || egress.CurrentValue.AllowedHosts.Length == 0)
            errors.Add("EgressPolicy must require a non-empty allowlist in Production.");

        if (sandbox.CurrentValue.RequireIsolationForWriteTools
            && !string.Equals(sandbox.CurrentValue.Mode, "isolated-process", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(sandbox.CurrentValue.Mode, "container", StringComparison.OrdinalIgnoreCase))
            errors.Add("RuntimeSandbox:Mode must be isolated-process or container in Production.");

        if (errors.Count > 0)
            throw new InvalidOperationException("Production P0 security validation failed: " + string.Join(" | ", errors));

        log.LogInformation("Production P0 security validation passed.");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static void RejectInlineSecret(string? value, string key, List<string> errors)
    {
        if (!string.IsNullOrWhiteSpace(value))
            errors.Add($"{key} must come from external secret provider, not appsettings/environment inline value.");
    }
}
