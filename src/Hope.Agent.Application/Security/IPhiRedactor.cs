namespace Hope.Agent.Application.Security;

/// <summary>
/// Masks personally-identifiable / protected-health information so the redacted
/// text is safe for audit logs, telemetry, and long-term storage.
/// </summary>
public interface IPhiRedactor
{
    string Redact(string input);
}
