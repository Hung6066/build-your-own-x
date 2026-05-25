using System.Text.RegularExpressions;
using Hope.Agent.Application.Observability;
using Hope.Agent.Application.Security;
using Microsoft.Extensions.Logging;

namespace Hope.Agent.Infrastructure.Security;

/// <summary>
/// Regex-based implementation of <see cref="IOutputShield"/>.
/// Screens for: private keys, bearer tokens, OpenAI/Anthropic/GitHub API keys,
/// generic high-entropy API keys, and database connection strings.
/// </summary>
internal sealed partial class RegexOutputShield(ILogger<RegexOutputShield> log) : IOutputShield
{
    // ── Regex patterns ─────────────────────────────────────────────────────────

    // PEM private keys
    [GeneratedRegex(@"-----BEGIN (RSA |EC |OPENSSH |)PRIVATE KEY-----[\s\S]{20,}?-----END \1PRIVATE KEY-----", RegexOptions.IgnoreCase)]
    private static partial Regex PrivateKeyRx();

    // Bearer tokens in prose ("Authorization: Bearer eyJ..." or "token: Bearer ...")
    [GeneratedRegex(@"(?i)bearer\s+([A-Za-z0-9\-_]{30,})", RegexOptions.None)]
    private static partial Regex BearerTokenRx();

    // OpenAI keys: sk-... (32-48 chars)
    [GeneratedRegex(@"\bsk-[A-Za-z0-9]{32,48}\b")]
    private static partial Regex OpenAiKeyRx();

    // Anthropic keys: sk-ant-...
    [GeneratedRegex(@"\bsk-ant-[A-Za-z0-9\-_]{20,60}\b")]
    private static partial Regex AnthropicKeyRx();

    // GitHub tokens (classic + fine-grained)
    [GeneratedRegex(@"\b(ghp_|gho_|github_pat_)[A-Za-z0-9_]{20,100}\b")]
    private static partial Regex GitHubTokenRx();

    // Connection strings containing passwords
    [GeneratedRegex(@"(?i)(password|pwd)\s*=\s*[^;'""\s]{6,}", RegexOptions.None)]
    private static partial Regex ConnStringPasswordRx();

    // PostgreSQL/MongoDB URIs with credentials
    [GeneratedRegex(@"(?i)(postgresql|mongodb(\+srv)?|redis|mysql|mssql)://[^@\s]{3,}@", RegexOptions.None)]
    private static partial Regex CredentialedUriRx();

    private static readonly (string Label, Regex Pattern, string Replacement)[] Patterns =
    [
        ("private_key",         PrivateKeyRx(),        "[REDACTED:PRIVATE_KEY]"),
        ("bearer_token",        BearerTokenRx(),       "Bearer [REDACTED]"),
        ("openai_key",          OpenAiKeyRx(),         "[REDACTED:API_KEY]"),
        ("anthropic_key",       AnthropicKeyRx(),      "[REDACTED:API_KEY]"),
        ("github_token",        GitHubTokenRx(),       "[REDACTED:TOKEN]"),
        ("db_conn_password",    ConnStringPasswordRx(),"Password=[REDACTED]"),
        ("credentialed_uri",    CredentialedUriRx(),   "[REDACTED:DB_URI]"),
    ];

    public OutputShieldResult Inspect(string output)
    {
        if (string.IsNullOrEmpty(output))
            return new OutputShieldResult(false, output, []);

        try
        {
            var detections = new List<string>();
            var safe = output;

            foreach (var (label, rx, replacement) in Patterns)
            {
                if (rx.IsMatch(safe))
                {
                    detections.Add(label);
                    safe = rx.Replace(safe, replacement);
                }
            }

            if (detections.Count > 0)
            {
                log.LogWarning(
                    "OutputShield: {Count} credential pattern(s) redacted from LLM output: {Detections}",
                    detections.Count, string.Join(", ", detections));
                HopeMeters.PromptShieldBlocks.Add(1, new KeyValuePair<string, object?>("reason", "output:" + detections[0]));
            }

            return new OutputShieldResult(detections.Count > 0, safe, detections);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "OutputShield failed; returning original output unmodified");
            return new OutputShieldResult(false, output, []);
        }
    }
}
