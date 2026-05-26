namespace Hope.Agent.Application.Governance;

/// <summary>Decision returned by the governance gate for each intent evaluation.</summary>
public sealed record GovernanceDecision(
    bool Allowed,
    string PolicyName,
    string? MatchedRule = null,
    string? DenyReason = null);

/// <summary>
/// Application-layer governance abstraction backed by Microsoft.AgentGovernance (AGT).
/// Two responsibilities:
///   1. Intent routing gate — verifies an intent is permitted before an agent role executes.
///   2. PHI scan — detects forbidden data markers in user input (replaces hard-coded arrays).
/// </summary>
public interface IGovernanceGate
{
    /// <summary>
    /// Evaluate whether <paramref name="intent"/> is permitted for <paramref name="agentDid"/>.
    /// Returns <see cref="GovernanceDecision.Allowed"/> = false when a loaded policy denies
    /// the intent. Fails closed on evaluation errors.
    /// </summary>
    ValueTask<GovernanceDecision> EvaluateIntentAsync(
        string agentDid,
        string intent,
        IReadOnlyDictionary<string, object?>? context = null,
        CancellationToken ct = default);

    /// <summary>
    /// Scan <paramref name="input"/> for configured forbidden patterns (PHI markers).
    /// Returns the list of matched patterns; empty list means clean input.
    /// </summary>
    IReadOnlyList<string> ScanForForbiddenPatterns(string input);
}
