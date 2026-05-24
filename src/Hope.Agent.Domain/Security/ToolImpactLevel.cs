namespace Hope.Agent.Domain.Security;

/// <summary>
/// Classifies the side-effects of a tool invocation. Drives the approval policy.
/// </summary>
public enum ToolImpactLevel
{
    /// <summary>Pure read-only lookup. No side effects. Auto-approved.</summary>
    ReadOnly = 0,

    /// <summary>Writes data or schedules something. May require human approval.</summary>
    Write = 1,

    /// <summary>Irreversible / safety-critical (medication, surgery, discharge). Always requires explicit approval.</summary>
    Critical = 2,
}
