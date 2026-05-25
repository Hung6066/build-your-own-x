namespace Hope.Agent.Application.Security;

/// <summary>
/// Validates outbound URLs before the agent makes HTTP connections.
/// Inspired by NemoClaw's blueprint/ssrf.ts — blocks private IPs,
/// loopback, link-local, and cloud-metadata endpoints.
/// </summary>
public interface ISsrfGuard
{
    SsrfCheckResult Validate(string url);
    SsrfCheckResult Validate(Uri uri);
}

/// <param name="Safe">True when the URL is allowed.</param>
/// <param name="BlockReason">Non-null reason when the URL is blocked.</param>
public sealed record SsrfCheckResult(bool Safe, string? BlockReason);
