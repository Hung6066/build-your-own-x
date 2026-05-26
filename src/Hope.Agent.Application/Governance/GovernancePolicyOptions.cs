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
