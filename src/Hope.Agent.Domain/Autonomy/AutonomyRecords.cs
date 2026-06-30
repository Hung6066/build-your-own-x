namespace Hope.Agent.Domain.Autonomy;

public enum AutonomyRiskLevel
{
    Low = 0,
    Medium = 1,
    High = 2,
    Critical = 3,
}

public enum AutonomyPolicyDecision
{
    SuggestOnly = 0,
    AutoExecute = 1,
    RequireApproval = 2,
    AutoDeny = 3,
}

public enum AgentDecisionStatus
{
    Suggested = 0,
    Queued = 1,
    AutoExecuted = 2,
    RequiresApproval = 3,
    Approved = 4,
    Denied = 5,
    Failed = 6,
}

public enum AutonomousActionStatus
{
    Pending = 0,
    Approved = 1,
    Executing = 2,
    Succeeded = 3,
    Failed = 4,
    Denied = 5,
    Cancelled = 6,
}

public enum AutonomyGoalStatus
{
    Proposed = 0,
    Queued = 1,
    InProgress = 2,
    Completed = 3,
    Failed = 4,
    Cancelled = 5,
}

public enum AutonomyLearningFactKind
{
    OutcomePattern = 0,
    SafetySignal = 1,
    CareGapPattern = 2,
    UserPreference = 3,
}

public enum AutonomyControlStatus
{
    Pending = 0,
    Passed = 1,
    Failed = 2,
    Warning = 3,
    Executed = 4,
}

public enum AutonomyDriftSeverity
{
    Info = 0,
    Warning = 1,
    Critical = 2,
}

public sealed class AgentDecision
{
    public Guid Id { get; set; }
    public Guid? TenantId { get; set; }
    public string DecisionId { get; set; } = string.Empty;
    public Guid UserId { get; set; }
    public Guid? PatientId { get; set; }
    public Guid? ConversationId { get; set; }
    public string Intent { get; set; } = string.Empty;
    public string? AgentProfile { get; set; }
    public string InputSummary { get; set; } = string.Empty;
    public string? MemoryRefsJson { get; set; }
    public string? EvidenceJson { get; set; }
    public string? ProposedActionJson { get; set; }
    public AutonomyRiskLevel RiskLevel { get; set; }
    public double Confidence { get; set; }
    public AutonomyPolicyDecision PolicyDecision { get; set; }
    public AgentDecisionStatus DecisionStatus { get; set; }
    public string? Reason { get; set; }
    public string DeploymentVersion { get; set; } = "dev";
    public string PromptVersion { get; set; } = "hope-runtime-prompt-v1";
    public string ModelVersion { get; set; } = "unknown";
    public string ToolsetVersion { get; set; } = "hope-tools-v1";
    public string PolicyVersion { get; set; } = "hope-policy-v1";
    public DateTimeOffset CreatedAt { get; set; }
    public string? CorrelationId { get; set; }
}

public sealed class AutonomyEvalGateRun
{
    public Guid Id { get; set; }
    public Guid? TenantId { get; set; }
    public string GateId { get; set; } = string.Empty;
    public string SuiteName { get; set; } = string.Empty;
    public string DeploymentVersion { get; set; } = "dev";
    public string PromptVersion { get; set; } = "hope-runtime-prompt-v1";
    public string ModelVersion { get; set; } = "unknown";
    public string ToolsetVersion { get; set; } = "hope-tools-v1";
    public string PolicyVersion { get; set; } = "hope-policy-v1";
    public bool Passed { get; set; }
    public double PassRate { get; set; }
    public string MetricsJson { get; set; } = "{}";
    public string? Reason { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string? CorrelationId { get; set; }
}

public sealed class AutonomyDriftSignal
{
    public Guid Id { get; set; }
    public string SignalId { get; set; } = string.Empty;
    public string SignalType { get; set; } = string.Empty;
    public AutonomyDriftSeverity Severity { get; set; }
    public double Score { get; set; }
    public string BaselineJson { get; set; } = "{}";
    public string CurrentJson { get; set; } = "{}";
    public AutonomyControlStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string? CorrelationId { get; set; }
}

public sealed class AutonomyCompensationRecord
{
    public Guid Id { get; set; }
    public string CompensationId { get; set; } = string.Empty;
    public string ActionId { get; set; } = string.Empty;
    public string ToolName { get; set; } = string.Empty;
    public string ArgumentsJson { get; set; } = "{}";
    public AutonomyControlStatus Status { get; set; }
    public string? ResultJson { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ExecutedAt { get; set; }
    public string? CorrelationId { get; set; }
}

public sealed class AutonomyReviewRecord
{
    public Guid Id { get; set; }
    public string ReviewId { get; set; } = string.Empty;
    public string DecisionId { get; set; } = string.Empty;
    public string ReviewerProfile { get; set; } = string.Empty;
    public AutonomyControlStatus Verdict { get; set; }
    public double Confidence { get; set; }
    public string Notes { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public string? CorrelationId { get; set; }
}

public sealed class AutonomyGoal
{
    public Guid Id { get; set; }
    public Guid? TenantId { get; set; }
    public string GoalId { get; set; } = string.Empty;
    public Guid? PatientId { get; set; }
    public Guid UserId { get; set; }
    public string GoalType { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string EvidenceJson { get; set; } = "[]";
    public double PriorityScore { get; set; }
    public double Confidence { get; set; }
    public AutonomyRiskLevel MaxAllowedRisk { get; set; }
    public AutonomyGoalStatus Status { get; set; }
    public string? DecisionId { get; set; }
    public string? Reason { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? CorrelationId { get; set; }
}

public sealed class AutonomyReflection
{
    public Guid Id { get; set; }
    public Guid? TenantId { get; set; }
    public string ReflectionId { get; set; } = string.Empty;
    public string? GoalId { get; set; }
    public string? DecisionId { get; set; }
    public string? ActionId { get; set; }
    public Guid? PatientId { get; set; }
    public bool Succeeded { get; set; }
    public string Summary { get; set; } = string.Empty;
    public string LessonsJson { get; set; } = "[]";
    public double ConfidenceDelta { get; set; }
    public string? CorrelationId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class AutonomyLearningFact
{
    public Guid Id { get; set; }
    public Guid? TenantId { get; set; }
    public string FactId { get; set; } = string.Empty;
    public AutonomyLearningFactKind Kind { get; set; }
    public string Key { get; set; } = string.Empty;
    public string ValueJson { get; set; } = "{}";
    public double Confidence { get; set; }
    public string Source { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastObservedAt { get; set; }
}

public sealed class AutonomousAction
{
    public Guid Id { get; set; }
    public Guid? TenantId { get; set; }
    public string ActionId { get; set; } = string.Empty;
    public string DecisionId { get; set; } = string.Empty;
    public string ToolName { get; set; } = string.Empty;
    public string ArgumentsJson { get; set; } = "{}";
    public AutonomyRiskLevel RiskLevel { get; set; }
    public double Confidence { get; set; }
    public AutonomousActionStatus Status { get; set; }
    public DateTimeOffset? ScheduledFor { get; set; }
    public DateTimeOffset? ExecutedAt { get; set; }
    public string? ResultJson { get; set; }
    public string? Error { get; set; }
    public int AttemptCount { get; set; }
    public string? IdempotencyKey { get; set; }
    public string QueueBackend { get; set; } = "postgres-ledger";
    public bool DispatchedToDurableQueue { get; set; }
    public DateTimeOffset? DispatchedAt { get; set; }
    public string? CompensationToolName { get; set; }
    public string? CompensationArgumentsJson { get; set; }
    public string DeploymentVersion { get; set; } = "dev";
    public string PromptVersion { get; set; } = "hope-runtime-prompt-v1";
    public string ModelVersion { get; set; } = "unknown";
    public string ToolsetVersion { get; set; } = "hope-tools-v1";
    public string PolicyVersion { get; set; } = "hope-policy-v1";
    public DateTimeOffset CreatedAt { get; set; }
    public string? CorrelationId { get; set; }
}
