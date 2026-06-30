using Hope.Agent.Domain.Autonomy;

namespace Hope.Agent.Application.Security;

public enum DataSensitivity
{
    Public = 0,
    Internal = 1,
    Confidential = 2,
    Phi = 3,
    Restricted = 4,
}

public sealed class EnterpriseDataPerimeterOptions
{
    public const string SectionName = "EnterpriseDataPerimeter";
    public bool Enabled { get; init; } = true;
    public string DefaultRegion { get; init; } = "us";
    public string[] AllowedRegions { get; init; } = ["us"];
    public Dictionary<string, TenantDataPerimeterPolicy> Tenants { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string[]> PurposeAccess { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public bool RequirePurposeForPhi { get; init; } = true;
    public bool RequireBreakGlassReview { get; init; } = true;
    public int BreakGlassReviewDueHours { get; init; } = 24;
}

public sealed class TenantDataPerimeterPolicy
{
    public string Region { get; init; } = "us";
    public string Classification { get; init; } = "Phi";
    public string[] AllowedPurposes { get; init; } = ["treatment", "operations", "audit"];
    public string[] AllowedProviders { get; init; } = ["local"];
    public bool RequireLocalModelForPhi { get; init; } = true;
}

public sealed class SecureModelRoutingOptions
{
    public const string SectionName = "SecureModelRouting";
    public bool Enabled { get; init; } = true;
    public string LocalFallbackProvider { get; init; } = "local";
    public string[] GlobalModelAllowlist { get; init; } = ["local"];
    public string[] PhiApprovedProviders { get; init; } = ["local"];
    public Dictionary<string, string[]> TenantProviderAllowlist { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string[]> RiskProviderAllowlist { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public bool BlockCostLatencyRouterForPhi { get; init; } = true;
}

public sealed class AdversarialSimulationOptions
{
    public const string SectionName = "AdversarialSimulation";
    public bool Enabled { get; init; } = true;
    public int IntervalHours { get; init; } = 24;
    public string TargetEnvironment { get; init; } = "staging";
    public string[] Suites { get; init; } =
    [
        "prompt_injection",
        "data_exfiltration",
        "unauthorized_tool_call",
        "privilege_escalation",
        "hallucinated_citation",
        "cross_tenant_leakage",
    ];
    public double MinPassRate { get; init; } = 1.0;
    public bool ReplayAgainstCanary { get; init; } = true;
    public bool EmitDriftSignalOnFailure { get; init; } = true;
}

public sealed class IncidentResponseOptions
{
    public const string SectionName = "IncidentResponse";
    public bool Enabled { get; init; } = true;
    public string[] Runbooks { get; init; } =
    [
        "data_leakage",
        "wrong_tool_execution",
        "compromised_token",
        "prompt_injection_campaign",
    ];
    public bool AutoDisableAutonomyOnSeverityHigh { get; init; } = true;
    public bool AutoDisableToolOnWrongExecution { get; init; } = true;
    public bool EnableForensicExport { get; init; } = true;
    public string ForensicExportTarget { get; init; } = "object-storage://incident-forensics";
}

public sealed record DataPerimeterRequest(
    Guid? TenantId,
    string Purpose,
    DataSensitivity Sensitivity,
    string Region,
    string ActorRole,
    bool BreakGlass = false);

public sealed record DataPerimeterDecision(
    bool Allowed,
    string Reason,
    string PolicyVersion,
    string? RequiredReview = null,
    IReadOnlyDictionary<string, string>? Explain = null);

public interface IDataPerimeterService
{
    DataPerimeterDecision Evaluate(DataPerimeterRequest request);
}

public sealed record ModelRoutingPolicyRequest(
    Guid? TenantId,
    string Intent,
    string Provider,
    string Model,
    AutonomyRiskLevel RiskLevel,
    DataSensitivity Sensitivity,
    bool CostLatencyOptimized);

public sealed record ModelRoutingPolicyDecision(
    bool Allowed,
    string Provider,
    string Model,
    string Reason,
    string PolicyVersion);

public interface ISecureModelRoutingPolicy
{
    ModelRoutingPolicyDecision Evaluate(ModelRoutingPolicyRequest request);
}

public sealed record ContextProvenanceWrite(
    Guid? TenantId,
    Guid? PatientId,
    Guid? ConversationId,
    string? DecisionId,
    string? ActionId,
    string CorrelationId,
    string AnswerHash,
    string RetrievalQuery,
    string SourceManifestJson,
    string DroppedContextJson,
    int TokenBudget,
    string Purpose,
    DataSensitivity Sensitivity,
    string PolicyVersion);

public interface IContextProvenanceStore
{
    Task<Guid> AddAsync(ContextProvenanceWrite write, CancellationToken ct);
}

public sealed record IncidentOpenRequest(
    Guid? TenantId,
    string IncidentType,
    string Severity,
    string Summary,
    string? CorrelationId,
    string? AgentProfile,
    string? ToolName);

public interface IIncidentResponseService
{
    Task<Guid> OpenAsync(IncidentOpenRequest request, CancellationToken ct);
    Task<object> BuildForensicExportAsync(Guid incidentId, CancellationToken ct);
}
