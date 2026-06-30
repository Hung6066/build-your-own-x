using Hope.Agent.Domain.Autonomy;

namespace Hope.Agent.Application.Autonomy;

public sealed class AutonomyOptions
{
    public const string Section = "Autonomy";
    public bool Enabled { get; set; } = true;
    public string Mode { get; set; } = "SuggestOnly";
    public AutonomyRiskLevel AutoExecuteMaxRisk { get; set; } = AutonomyRiskLevel.Low;
    public double MinConfidenceForAutoExecute { get; set; } = 0.85;
    public bool RequireApprovalForClinicalWrite { get; set; } = true;
    public bool RequireApprovalForPhiExport { get; set; } = true;
    public bool RequireApprovalForMedicationChange { get; set; } = true;
    public int RequireApprovalForEmergencySeverityAtLeast { get; set; } = 4;
}

public sealed class AutonomyDailyReviewOptions
{
    public const string Section = "AutonomyDailyReview";
    public bool Enabled { get; set; }
    public string TimeUtc { get; set; } = "00:30";
    public int MaxPatientsPerRun { get; set; } = 100;
    public int LookbackDays { get; set; } = 60;
    public Guid? UserId { get; set; }
    public string Goal { get; set; } = "Daily autonomy review: evaluate old patient data and suggest safe follow-up actions.";
}

public sealed class AutonomyLevel5Options
{
    public const string Section = "AutonomyLevel5";
    public bool Enabled { get; set; }
    public int MaxActionsPerPatientPerDay { get; set; } = 3;
    public int MaxTotalActionsPerDay { get; set; } = 1000;
    public int AutoPauseFailureThresholdPerHour { get; set; } = 10;
    public bool RequireOutcomeVerification { get; set; } = true;
    public bool AllowClinicalCriticalAutonomy { get; set; }
    public bool RequireEvalGateForAutoExecute { get; set; } = true;
    public double MinEvalGatePassRate { get; set; } = 0.9;
    public double MaxAllowedDriftScore { get; set; } = 0.25;
    public AutonomyRiskLevel RequireSecondReviewForRiskAtLeast { get; set; } = AutonomyRiskLevel.High;
    public bool EnableCompensation { get; set; } = true;
    public int ConfidenceCalibrationWindowDays { get; set; } = 14;
}

public sealed class AutonomyAgiLikeOptions
{
    public const string Section = "AutonomyAgiLike";
    public bool Enabled { get; set; }
    public int MaxGoalsPerRun { get; set; } = 25;
    public int MinEvidenceItems { get; set; } = 2;
    public double MinGoalConfidence { get; set; } = 0.72;
    public bool AutoCreateLearningFacts { get; set; } = true;
    public AutonomyRiskLevel MaxGoalRisk { get; set; } = AutonomyRiskLevel.Medium;
}

public sealed record AutonomyEvaluationRequest(
    Guid UserId,
    Guid? PatientId,
    Guid? ConversationId,
    string Intent,
    string? AgentProfile,
    string Input,
    string? ToolName,
    string? ArgumentsJson,
    double Confidence,
    string? CorrelationId = null,
    Guid? TenantId = null);

public sealed record AutonomyEvaluationResult(
    AutonomyRiskLevel RiskLevel,
    AutonomyPolicyDecision PolicyDecision,
    AgentDecisionStatus DecisionStatus,
    string Reason);

public sealed record AgentDecisionWrite(
    Guid UserId,
    Guid? PatientId,
    Guid? ConversationId,
    string Intent,
    string? AgentProfile,
    string InputSummary,
    string? MemoryRefsJson,
    string? EvidenceJson,
    string? ProposedActionJson,
    AutonomyRiskLevel RiskLevel,
    double Confidence,
    AutonomyPolicyDecision PolicyDecision,
    AgentDecisionStatus DecisionStatus,
    string? Reason,
    string? CorrelationId,
    Guid? TenantId = null,
    string? DeploymentVersion = null,
    string? PromptVersion = null,
    string? ModelVersion = null,
    string? ToolsetVersion = null,
    string? PolicyVersion = null);

public sealed record AutonomousActionWrite(
    string DecisionId,
    string ToolName,
    string ArgumentsJson,
    AutonomyRiskLevel RiskLevel,
    double Confidence,
    AutonomousActionStatus Status,
    DateTimeOffset? ScheduledFor,
    string? CorrelationId,
    Guid? TenantId = null,
    string? IdempotencyKey = null,
    string? QueueBackend = null,
    string? CompensationToolName = null,
    string? CompensationArgumentsJson = null,
    string? DeploymentVersion = null,
    string? PromptVersion = null,
    string? ModelVersion = null,
    string? ToolsetVersion = null,
    string? PolicyVersion = null);

public sealed record AutonomyGoalWrite(
    Guid? PatientId,
    Guid UserId,
    string GoalType,
    string Description,
    string EvidenceJson,
    double PriorityScore,
    double Confidence,
    AutonomyRiskLevel MaxAllowedRisk,
    AutonomyGoalStatus Status,
    string? DecisionId,
    string? Reason,
    string? CorrelationId);

public sealed record AutonomyReflectionWrite(
    string? GoalId,
    string? DecisionId,
    string? ActionId,
    Guid? PatientId,
    bool Succeeded,
    string Summary,
    string LessonsJson,
    double ConfidenceDelta,
    string? CorrelationId);

public sealed record AutonomyLearningFactWrite(
    AutonomyLearningFactKind Kind,
    string Key,
    string ValueJson,
    double Confidence,
    string Source);

public sealed record PatientTimelineItem(
    string Source,
    string Type,
    DateTimeOffset OccurredAt,
    string Summary,
    string? ReferenceId = null);

public sealed record PatientTimeline(Guid PatientId, IReadOnlyList<PatientTimelineItem> Items);

public sealed record AgentSuggestion(
    string Type,
    string Summary,
    AutonomyRiskLevel RiskLevel,
    double Confidence,
    AutonomyPolicyDecision PolicyDecision,
    object ProposedAction);

public sealed record AgentSuggestionResult(
    string DecisionId,
    Guid PatientId,
    IReadOnlyList<AgentSuggestion> Suggestions);

public sealed record AutonomyAgiLikeRunResult(
    int GoalsCreated,
    int SuggestionsCreated,
    int ReflectionsCreated,
    int LearningFactsCreated,
    string Mode);

public sealed record AutonomyAgiLikeStatus(
    bool Enabled,
    int OpenGoals,
    int CompletedGoalsToday,
    int ReflectionsToday,
    int LearningFacts,
    int ActionsSucceededToday,
    int ActionsFailedToday);

public sealed record AutonomyEvalGateResult(
    string GateId,
    bool Passed,
    double PassRate,
    string Reason,
    object Metrics);

public sealed record AutonomyDriftResult(
    string SignalId,
    AutonomyDriftSeverity Severity,
    double Score,
    string Reason);

public sealed record AutonomyReadinessStatus(
    bool Ready,
    double LastEvalPassRate,
    double CurrentDriftScore,
    int CriticalSignalsLastDay,
    string Reason);

public sealed record AutonomyReviewResult(
    bool Passed,
    AutonomyControlStatus Verdict,
    double Confidence,
    string Notes);

public sealed record AutonomyCompensationResult(
    string? CompensationId,
    bool Created,
    bool Executed,
    string Reason);

public interface IAgentDecisionStore
{
    Task<AgentDecision> AddAsync(AgentDecisionWrite decision, CancellationToken ct);
    Task<IReadOnlyList<AgentDecision>> QueryAsync(Guid? patientId, Guid? userId, DateTimeOffset from, DateTimeOffset until, int take, CancellationToken ct);
    Task<AgentDecision?> GetByDecisionIdAsync(string decisionId, CancellationToken ct);
    Task UpdateStatusAsync(string decisionId, AgentDecisionStatus status, string? reason, CancellationToken ct);
}

public interface IAutonomousActionStore
{
    Task<AutonomousAction> AddAsync(AutonomousActionWrite action, CancellationToken ct);
    Task<AutonomousAction?> GetByActionIdAsync(string actionId, CancellationToken ct);
    Task<IReadOnlyList<AutonomousAction>> QueryAsync(AutonomousActionStatus? status, DateTimeOffset from, DateTimeOffset until, int take, CancellationToken ct);
    Task<IReadOnlyList<AutonomousAction>> DueAsync(DateTimeOffset now, int take, CancellationToken ct);
    Task UpdateAsync(AutonomousAction action, CancellationToken ct);
}

public interface IAutonomyGoalStore
{
    Task<AutonomyGoal> AddAsync(AutonomyGoalWrite goal, CancellationToken ct);
    Task<IReadOnlyList<AutonomyGoal>> QueryAsync(Guid? patientId, AutonomyGoalStatus? status, DateTimeOffset from, DateTimeOffset until, int take, CancellationToken ct);
    Task UpdateStatusAsync(string goalId, AutonomyGoalStatus status, string? decisionId, string? reason, CancellationToken ct);
}

public interface IAutonomyReflectionStore
{
    Task<AutonomyReflection> AddAsync(AutonomyReflectionWrite reflection, CancellationToken ct);
    Task<IReadOnlyList<AutonomyReflection>> QueryAsync(Guid? patientId, DateTimeOffset from, DateTimeOffset until, int take, CancellationToken ct);
}

public interface IAutonomyLearningFactStore
{
    Task<AutonomyLearningFact> UpsertAsync(AutonomyLearningFactWrite fact, CancellationToken ct);
    Task<IReadOnlyList<AutonomyLearningFact>> QueryAsync(AutonomyLearningFactKind? kind, int take, CancellationToken ct);
}

public interface IAutonomyDecisionService
{
    AutonomyEvaluationResult Evaluate(AutonomyEvaluationRequest request);
    Task<AgentDecision> RecordDecisionAsync(AgentDecisionWrite decision, CancellationToken ct);
}

public interface IPatientTimelineService
{
    Task<PatientTimeline> GetTimelineAsync(Guid patientId, int take, CancellationToken ct);
}

public interface IAgentSuggestionService
{
    Task<AgentSuggestionResult> SuggestAsync(Guid patientId, Guid userId, string goal, string? correlationId, CancellationToken ct);
}

public interface IAutonomousActionExecutor
{
    Task ExecuteDueAsync(CancellationToken ct);
}

public interface IAutonomyDailyReviewService
{
    Task<int> RunOnceAsync(DateTimeOffset runAt, bool force, CancellationToken ct);
}

public sealed record AutonomyBudgetDecision(bool Allowed, string Reason);

public sealed record AutonomyOutcomeVerification(bool Verified, string Reason);

public interface IAutonomySafetyBudget
{
    Task<AutonomyBudgetDecision> CheckAsync(Guid? patientId, string toolName, CancellationToken ct);
}

public interface IAutonomyOutcomeVerifier
{
    AutonomyOutcomeVerification Verify(string toolName, string argumentsJson, string resultJson);
}

public interface IAutonomyAgiLikeService
{
    Task<AutonomyAgiLikeRunResult> RunOnceAsync(Guid userId, bool force, CancellationToken ct);
    Task<AutonomyAgiLikeStatus> GetStatusAsync(CancellationToken ct);
}

public interface IAutonomyLevel5ControlService
{
    Task<AutonomyEvalGateResult> RunEvalGateAsync(string suiteName, string? correlationId, CancellationToken ct);
    Task<AutonomyDriftResult> DetectDriftAsync(string? correlationId, CancellationToken ct);
    Task<AutonomyReadinessStatus> GetReadinessAsync(CancellationToken ct);
    Task<double> CalibrateConfidenceAsync(string toolName, double baseConfidence, CancellationToken ct);
    Task<AutonomyReviewResult> ReviewAsync(string decisionId, AutonomyRiskLevel risk, string input, string? actionJson, string? correlationId, CancellationToken ct);
    Task<AutonomyCompensationResult> CreateCompensationAsync(AutonomousAction action, string reason, CancellationToken ct);
}
