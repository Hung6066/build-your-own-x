namespace Hope.Agent.Application.Security;

/// <summary>
/// Detects and neutralises prompt-injection attempts in user-supplied text
/// before it is sent to an LLM or used to build a system prompt.
/// </summary>
public interface IPromptShield
{
    PromptShieldResult Inspect(string input);
}

public sealed record PromptShieldResult(bool Allowed, string SanitizedInput, IReadOnlyList<string> Reasons);
