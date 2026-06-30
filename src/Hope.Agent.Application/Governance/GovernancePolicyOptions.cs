namespace Hope.Agent.Application.Governance;

/// <summary>
/// Configuration for the AGT governance gate, externalising what was previously
/// hard-coded string arrays in <c>ComplianceAgent</c> and <c>ClinicalAgent</c>.
///
/// Bind via <c>appsettings.json</c> section <c>"Governance:Policies"</c> to
/// override any default list at deployment time without a code change.
/// </summary>
public sealed class GovernancePolicyOptions
{
    public const string SectionName = "Governance:Policies";

    /// <summary>
    /// Substring patterns that indicate PHI presence in user input.
    /// Loaded into <c>DetectionConfig.CustomPatterns</c> of AGT's PromptInjectionDetector.
    /// </summary>
    public string[] PhiMarkers { get; init; } =
    [
        "ssn",
        "social security",
        "credit card",
        "passport",
    ];

    /// <summary>
    /// Keywords (English + Vietnamese) that, when detected in a clinical agent's
    /// output, trigger an emergency handoff to the <c>EmergencyAgent</c>.
    /// </summary>
    public string[] EmergencyTriggers { get; init; } =
    [
        "stroke", "đột quỵ",
        "myocardial infarction", "nhồi máu cơ tim", "heart attack",
        "sepsis", "nhiễm khuẩn huyết",
        "cardiac arrest", "ngừng tim",
        "respiratory failure", "suy hô hấp",
        "code blue", "cấp cứu ngay",
        "immediate emergency", "life-threatening",
    ];

    /// <summary>
    /// Paths to AGT YAML policy files loaded at startup by <c>AgtGovernanceGate</c>.
    /// Relative paths are resolved from the working directory of the process.
    /// Files that do not exist emit a warning and are skipped (fail-open for dev;
    /// configure CI to ensure files are present in production).
    /// </summary>
    public string[] PolicyPaths { get; init; } =
    [
        "policies/routing/allowed-intents.yaml",
    ];

    /// <summary>
    /// AGT <c>DetectionConfig.Sensitivity</c> used by <c>AgtPromptShield</c>
    /// for the ML-assisted injection-detection layer (Phase 2).
    /// Valid values: "High" | "Medium" | "Low".
    /// Default is "High" — use "Medium" in development to reduce false positives.
    /// </summary>
    public string InjectionDetectionSensitivity { get; init; } = "High";
}

public sealed class AgentOwnershipOptions
{
    public const string SectionName = "Governance:Ownership";
    public string DefaultResponsibleRole { get; init; } = "clinical_operations_owner";
    public string DefaultApproverRole { get; init; } = "clinical_supervisor";
    public Dictionary<string, AgentOwnerPolicy> Agents { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class AgentOwnerPolicy
{
    public string ResponsibleRole { get; init; } = "clinical_operations_owner";
    public string ApproverRole { get; init; } = "clinical_supervisor";
    public string[] AccessRoles { get; init; } = [];
    public string EscalationRole { get; init; } = "admin";
}

public sealed class AgentOpsOptions
{
    public const string SectionName = "AgentOps";
    public string AlertChannel { get; init; } = "slack:clinical-ai-alerts";
    public AgentOpsAlertRule[] AlertRules { get; init; } =
    [
        new("prompt_injection_blocks", "hope_prompt_shield_blocks_total", "rate_5m > 5", "security", "slack"),
        new("tool_error_rate", "hope_tool_errors_total", "rate_5m > 10", "platform", "slack"),
        new("autonomy_not_ready", "level5_readiness", "ready == false", "clinical_operations", "pagerduty"),
        new("eval_gate_fail", "hope_eval_gate_passed", "latest == 0", "ai_safety_officer", "pagerduty"),
        new("latency_p95", "http_server_duration_p95_ms", "p95_5m > 3000", "platform", "slack"),
        new("llm_cost_spike", "hope_llm_cost_usd", "rate_1h > budget_hourly_rate * 2", "finance_ops", "slack"),
        new("queue_backlog", "hope_autonomous_actions_pending", "value > Runtime.QueueBacklogWarningThreshold", "platform", "slack"),
        new("approval_timeout", "hope_tool_approvals_pending_oldest_seconds", "value > Governance.ApprovalSla.EscalateAfterSeconds", "clinical_operations", "pagerduty"),
        new("blocked_tool_calls", "hope_security_blocked_tool_calls_total", "rate_5m > 5", "ai_safety_officer", "slack"),
        new("policy_denials", "hope_security_policy_denials_total", "rate_5m > 10", "ai_safety_officer", "slack"),
        new("phi_redaction_spike", "hope_security_phi_redactions_total", "rate_5m > 20", "privacy_officer", "pagerduty"),
        new("cross_tenant_access_denied", "hope_security_cross_tenant_access_denied_total", "rate_5m > 0", "security", "pagerduty"),
        new("suspicious_autonomy_action", "hope_security_suspicious_autonomy_actions_total", "rate_5m > 0", "ai_safety_officer", "pagerduty"),
        new("data_perimeter_denial", "hope_security_data_perimeter_denials_total", "rate_5m > 0", "privacy_officer", "pagerduty"),
        new("model_routing_policy_block", "hope_security_model_routing_blocks_total", "rate_5m > 0", "ai_safety_officer", "slack"),
        new("break_glass_open", "hope_security_break_glass_access_total", "rate_5m > 0", "security", "pagerduty"),
        new("security_incident_opened", "hope_security_incidents_opened_total", "rate_5m > 0", "security", "pagerduty"),
        new("adversarial_simulation_failed", "hope_security_adversarial_simulation_runs_total", "passed == false", "ai_safety_officer", "pagerduty"),
    ];
}

public sealed record AgentOpsAlertRule(string Name, string Metric, string Condition, string OwnerRole, string Route = "slack");

public sealed class AccessMatrixOptions
{
    public const string SectionName = "Governance:AccessMatrix";
    public Dictionary<string, string[]> Endpoints { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string[]> Tables { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ApprovalSlaOptions
{
    public const string SectionName = "Governance:ApprovalSla";
    public int DefaultTimeoutSeconds { get; init; } = 45;
    public int EscalateAfterSeconds { get; init; } = 300;
    public string DefaultEscalationRole { get; init; } = "clinical_supervisor";
    public Dictionary<string, ApprovalSlaPolicy> ByRisk { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ApprovalSlaPolicy
{
    public int TimeoutSeconds { get; init; } = 45;
    public int EscalateAfterSeconds { get; init; } = 300;
    public string EscalationRole { get; init; } = "clinical_supervisor";
}

public sealed class AgentVersionOptions
{
    public const string SectionName = "AgentVersion";
    public string DeploymentVersion { get; init; } = "dev";
    public string PromptVersion { get; init; } = "hope-runtime-prompt-v1";
    public string ToolsetVersion { get; init; } = "hope-tools-v1";
    public string PolicyVersion { get; init; } = "hope-policy-v1";
    public string ModelRoutingVersion { get; init; } = "hope-routing-v1";
    public string ModelVersion { get; init; } = "unknown";
}

public sealed class RuntimeScaleOptions
{
    public const string SectionName = "Runtime";
    public bool EnableHostedServices { get; init; } = true;
    public bool ApiAcceptsBackgroundJobs { get; init; } = true;
    public int MaxApiReplicas { get; init; } = 10;
    public int MaxWorkerReplicas { get; init; } = 20;
    public int QueueBacklogWarningThreshold { get; init; } = 1000;
    public int ApprovalBacklogWarningThreshold { get; init; } = 100;
    public int IngestionBacklogWarningThreshold { get; init; } = 1000;
    public string DurableQueueBackend { get; init; } = "Temporal/Kafka";
    public string LedgerQueueBackend { get; init; } = "Postgres";
    public bool PostgresQueueHighThroughputAllowed { get; init; }
    public string ReadReplicaConnectionName { get; init; } = "PostgresReadReplica";
    public string QueueSchedulingMode { get; init; } = "weighted-fair-priority";
    public int PerTenantWorkerShareFloorPercent { get; init; } = 5;
    public int CriticalRiskPriorityBoost { get; init; } = 100;
}

public sealed class AgentRegistryOptions
{
    public const string SectionName = "AgentRegistry";
    public Dictionary<string, AgentRegistryEntry> Agents { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class AgentRegistryEntry
{
    public string OwnerRole { get; init; } = "clinical_operations_owner";
    public string[] AllowedTools { get; init; } = [];
    public string[] EvalSuites { get; init; } = [];
    public string PromptVersion { get; init; } = "hope-runtime-prompt-v1";
    public string ModelVersion { get; init; } = "unknown";
    public string ToolsetVersion { get; init; } = "hope-tools-v1";
    public string PolicyVersion { get; init; } = "hope-policy-v1";
    public string MaxAutonomyRisk { get; init; } = "Medium";
    public string WorkflowDag { get; init; } = "";
}

public sealed class OrchestrationDagOptions
{
    public const string SectionName = "Orchestration:Dags";
    public Dictionary<string, OrchestrationDagSpec> Workflows { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class OrchestrationDagSpec
{
    public string[] Nodes { get; init; } = [];
    public string[] Edges { get; init; } = [];
    public string[] RequiredReviewers { get; init; } = [];
}

public sealed class TenantIsolationOptions
{
    public const string SectionName = "TenantIsolation";
    public bool RequireTenantIdForWrites { get; init; } = true;
    public bool EnforceTenantRbac { get; init; } = true;
    public bool RequireTenantScopedRetrieval { get; init; } = true;
    public string DefaultTenantHeader { get; init; } = "X-Tenant-Id";
}

public sealed class CostControlOptions
{
    public const string SectionName = "CostControl";
    public decimal DefaultMonthlyTenantBudgetUsd { get; init; } = 500m;
    public decimal DefaultMonthlyAgentBudgetUsd { get; init; } = 100m;
    public decimal DefaultMonthlyWorkflowBudgetUsd { get; init; } = 100m;
    public bool EnableRealtimeAlerts { get; init; } = true;
    public bool AutoDowngradeModelOnBudgetPressure { get; init; } = true;
    public bool AutoDowngradeModelOnHighLatency { get; init; } = true;
    public double BudgetPressureThreshold { get; init; } = 0.8;
    public int LatencyP95DowngradeThresholdMs { get; init; } = 3000;
    public Dictionary<string, decimal> TenantBudgetsUsd { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, decimal> AgentBudgetsUsd { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, decimal> WorkflowBudgetsUsd { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class DataLifecycleOptions
{
    public const string SectionName = "DataLifecycle";
    public int MemoryRetentionDays { get; init; } = 365;
    public int AuditRetentionDays { get; init; } = 2555;
    public int EvalRetentionDays { get; init; } = 730;
    public int DecisionRetentionDays { get; init; } = 2555;
    public bool PhiRedactionRequired { get; init; } = true;
    public bool ComplianceExportEnabled { get; init; } = true;
    public bool ComplianceDeleteEnabled { get; init; } = true;
    public int RagTraceHotRetentionDays { get; init; } = 90;
    public string ArchiveTarget { get; init; } = "object-storage";
}

public sealed class DatabaseScaleOptions
{
    public const string SectionName = "DatabaseScale";
    public bool EnablePartitionMaintenance { get; init; } = true;
    public int PartitionMonthsAhead { get; init; } = 3;
    public bool EnableRollups { get; init; } = true;
    public int RollupIntervalMinutes { get; init; } = 15;
    public bool PreferReadReplicaForDashboard { get; init; } = true;
    public bool EnableWeightedFairQueue { get; init; } = true;
    public int QueueTenantBatchLimit { get; init; } = 25;
}

public sealed class DeploymentSafetyOptions
{
    public const string SectionName = "DeploymentSafety";
    public string Strategy { get; init; } = "canary";
    public bool RequireEvalGateBeforeDeploy { get; init; } = true;
    public bool EnableShadowTraffic { get; init; } = true;
    public double CanaryTrafficPercent { get; init; } = 5;
    public bool AutoRollbackOnEvalFailure { get; init; } = true;
    public bool AutoRollbackOnOpsFailure { get; init; } = true;
}
