namespace Hope.Agent.Application.Security;

public sealed record PolicyInput(
    string Subject,
    IReadOnlyList<string> Roles,
    string Action,
    string Resource,
    string Risk,
    Guid? TenantId,
    IReadOnlyDictionary<string, string>? Attributes = null);

public sealed record PolicyDecision(
    bool Allow,
    string Effect,
    string PolicyName,
    string PolicyVersion,
    string BundleDigest,
    string RuleId,
    string Reason,
    IReadOnlyDictionary<string, string> Explain);

public interface IPolicyEngine
{
    PolicyDecision Evaluate(PolicyInput input);
}

public sealed class PolicyAsCodeOptions
{
    public const string SectionName = "PolicyAsCode";
    public bool Enabled { get; init; } = true;
    public string Engine { get; init; } = "cedar-lite";
    public string BundlePath { get; init; } = "policies/security/policy-bundle.json";
    public string BundleSignaturePath { get; init; } = "policies/security/policy-bundle.sig";
    public bool RequireSignedBundle { get; init; } = true;
    public string SigningKeyId { get; init; } = "local-dev-policy-key";
    public string SigningSecret { get; init; } = "local-dev-policy-signing-secret";
    public string DefaultVersion { get; init; } = "security-policy-v1";
}
