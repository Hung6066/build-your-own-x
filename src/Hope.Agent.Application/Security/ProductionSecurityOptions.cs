namespace Hope.Agent.Application.Security;

public sealed class ZeroTrustOptions
{
    public const string SectionName = "ZeroTrust";
    public bool RequireMtls { get; init; } = true;
    public bool RequireWorkloadIdentity { get; init; } = true;
    public bool RequireShortLivedInternalJwt { get; init; } = true;
    public int InternalJwtMaxLifetimeMinutes { get; init; } = 10;
    public string[] RequiredMtlsServices { get; init; } =
    [
        "api", "worker", "kafka", "postgres", "redis", "qdrant", "temporal"
    ];
    public string WorkloadIdentityProvider { get; init; } = "azure-workload-identity";
}

public sealed class SecretManagementOptions
{
    public const string SectionName = "Secrets";
    public bool RequireExternalSecretProvider { get; init; } = true;
    public string Provider { get; init; } = "AzureKeyVault";
    public bool RejectInlineProductionSecrets { get; init; } = true;
    public bool RequireKmsEnvelopeEncryption { get; init; } = true;
    public string KmsKeyId { get; init; } = string.Empty;
    public int KeyRotationDays { get; init; } = 90;
    public bool EnableTenantCryptoShred { get; init; } = true;
}

public sealed class DataPerimeterOptions
{
    public const string SectionName = "DataPerimeter";
    public bool RequirePostgresRls { get; init; } = true;
    public bool RequireTenantIdNotNull { get; init; } = true;
    public bool RequireQdrantTenantPayloadFilter { get; init; } = true;
    public bool RequireRedisKeyNamespace { get; init; } = true;
    public string RedisKeyPrefix { get; init; } = "hope:prod";
    public bool RequireRedisAcl { get; init; } = true;
}

public sealed class AuditImmutabilityOptions
{
    public const string SectionName = "AuditImmutability";
    public bool Enabled { get; init; } = true;
    public bool RequireWormArchive { get; init; } = true;
    public string WormArchiveUri { get; init; } = "s3://hope-audit-worm";
    public int VerifyIntervalMinutes { get; init; } = 1440;
    public int VerificationLookbackDays { get; init; } = 30;
}

public sealed class StorageEncryptionOptions
{
    public const string SectionName = "StorageEncryption";
    public bool RequireAtRestEncryption { get; init; } = true;
    public bool AtRestEnabled { get; init; }
    public string Provider { get; init; } = "platform-managed";
}

public sealed class RedisHighAvailabilityOptions
{
    public const string SectionName = "RedisHa";
    public bool Enabled { get; init; }
    public string ServiceName { get; init; } = string.Empty;
    public string[] Endpoints { get; init; } = [];
}

public sealed class DlpOptions
{
    public const string SectionName = "Dlp";
    public bool Enabled { get; init; } = true;
    public bool RedactPhiOnExternalChannels { get; init; } = true;
    public bool BlockPhiExportWithoutApproval { get; init; } = true;
    public string PhiExportWatermark { get; init; } = "PHI EXPORT - APPROVED - AUDITED";
    public string[] ExternalChannels { get; init; } = ["slack", "email", "zalo"];
}

public sealed class EgressPolicyOptions
{
    public const string SectionName = "EgressPolicy";
    public bool RequireAllowlist { get; init; } = true;
    public string[] AllowedHosts { get; init; } = [];
}

public sealed class RuntimeSandboxOptions
{
    public const string SectionName = "RuntimeSandbox";
    public bool RequireIsolationForWriteTools { get; init; } = true;
    public string Mode { get; init; } = "isolated-process";
    public int CpuMillicores { get; init; } = 500;
    public int MemoryMb { get; init; } = 256;
    public int NetworkEgressTimeoutMs { get; init; } = 10_000;
    public string HighRiskWorkerPool { get; init; } = "high-risk-tools";
    public Dictionary<string, bool> KillSwitch { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> ToolPools { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

public interface IEnvelopeEncryptionService
{
    Task<EnvelopeEncryptionResult> EncryptAsync(Guid? tenantId, string plaintext, string purpose, CancellationToken ct);
    Task<string> DecryptAsync(Guid? tenantId, EnvelopeEncryptionResult envelope, CancellationToken ct);
}

public sealed record EnvelopeEncryptionResult(
    string CiphertextBase64,
    string EncryptedDataKeyBase64,
    string KmsKeyId,
    string Algorithm,
    Guid? TenantId,
    string Purpose,
    DateTimeOffset CreatedAt);
