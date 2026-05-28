namespace Hope.Agent.Application.Security;

/// <summary>Outcome of an LLM-response egress inspection.</summary>
public sealed record EgressInspection(
    bool Allowed,
    string SanitizedResponse,
    IReadOnlyList<string> Reasons);

/// <summary>
/// Inspects an LLM-generated response immediately before it is returned to the
/// caller / streamed over SignalR. Closes the OWASP LLM06 (Sensitive Info
/// Disclosure) gap by stripping PHI that the model produced from a poisoned
/// context or by mistake.
/// </summary>
public interface IPromptEgressGuard
{
    /// <summary>
    /// Inspects the assistant response for PHI / cross-patient data / forbidden
    /// disclosures. Returns a sanitized version (PHI redacted) and a reasons list.
    /// </summary>
    EgressInspection Inspect(string response, EgressContext ctx);
}

/// <summary>
/// Caller context handed to <see cref="IPromptEgressGuard"/> so it can perform
/// cross-patient leak detection (response contains another patient's id?).
/// </summary>
public sealed record EgressContext(
    Guid CallerUserId,
    string? CallerSubject,
    IReadOnlyCollection<string> AllowedPatientIds);
