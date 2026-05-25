namespace Hope.Agent.Application.Security;

/// <summary>
/// Screens LLM-generated output for accidental secret/credential leakage
/// before the response is returned to clients (OWASP LLM06 — Sensitive Information Disclosure).
///
/// Unlike <see cref="IPromptShield"/> (which guards INPUT), this guards OUTPUT:
/// a jailbroken or hallucinating model might embed API keys, bearer tokens, or private keys
/// that ended up in its context window from environment variables, retrieved documents, etc.
/// </summary>
public interface IOutputShield
{
    /// <summary>
    /// Inspects <paramref name="output"/> for secret patterns.
    /// Returns a result with any detected patterns redacted.
    /// Never throws — on regex failure it returns the original output unmodified.
    /// </summary>
    OutputShieldResult Inspect(string output);
}

/// <param name="HasLeak">True if at least one credential pattern was detected.</param>
/// <param name="SafeContent">Output with secrets redacted (safe to return to client).</param>
/// <param name="Detections">Human-readable labels for each detected pattern type.</param>
public sealed record OutputShieldResult(bool HasLeak, string SafeContent, IReadOnlyList<string> Detections);
