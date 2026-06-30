namespace Hope.Agent.Domain.Security;

public sealed class ContextProvenanceRecord
{
    public Guid Id { get; set; }
    public Guid? TenantId { get; set; }
    public Guid? PatientId { get; set; }
    public Guid? ConversationId { get; set; }
    public string? DecisionId { get; set; }
    public string? ActionId { get; set; }
    public string CorrelationId { get; set; } = string.Empty;
    public string AnswerHash { get; set; } = string.Empty;
    public string RetrievalQuery { get; set; } = string.Empty;
    public string SourceManifestJson { get; set; } = "[]";
    public string DroppedContextJson { get; set; } = "[]";
    public int TokenBudget { get; set; }
    public string Purpose { get; set; } = "treatment";
    public string Sensitivity { get; set; } = "Phi";
    public string PolicyVersion { get; set; } = "hope-policy-v1";
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class SecurityIncidentRecord
{
    public Guid Id { get; set; }
    public Guid? TenantId { get; set; }
    public string IncidentType { get; set; } = string.Empty;
    public string Severity { get; set; } = "medium";
    public string Status { get; set; } = "open";
    public string Summary { get; set; } = string.Empty;
    public string? AgentProfile { get; set; }
    public string? ToolName { get; set; }
    public bool AutonomyDisabled { get; set; }
    public bool ToolDisabled { get; set; }
    public string RunbookJson { get; set; } = "[]";
    public string? ForensicExportJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    public string? CorrelationId { get; set; }
}

public sealed class BreakGlassAccessRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid ActorUserId { get; set; }
    public string Purpose { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public string Status { get; set; } = "pending_post_review";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ReviewDueAt { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }
    public string? Reviewer { get; set; }
    public string? CorrelationId { get; set; }
}

public sealed class AdversarialSimulationRun
{
    public Guid Id { get; set; }
    public Guid? TenantId { get; set; }
    public string SimulationId { get; set; } = string.Empty;
    public string TargetEnvironment { get; set; } = "staging";
    public string SuitesJson { get; set; } = "[]";
    public bool ReplayAgainstCanary { get; set; }
    public double PassRate { get; set; }
    public bool Passed { get; set; }
    public string FindingsJson { get; set; } = "[]";
    public string PolicyVersion { get; set; } = "hope-policy-v1";
    public DateTimeOffset CreatedAt { get; set; }
    public string? CorrelationId { get; set; }
}
