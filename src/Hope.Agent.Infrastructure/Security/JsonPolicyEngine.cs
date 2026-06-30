using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hope.Agent.Application.Security;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hope.Agent.Infrastructure.Security;

internal sealed class JsonPolicyEngine : IPolicyEngine
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly IOptionsMonitor<PolicyAsCodeOptions> options;
    private readonly IHostEnvironment environment;
    private readonly ILogger<JsonPolicyEngine> log;
    private PolicyBundle? cached;
    private string cachedDigest = string.Empty;

    public JsonPolicyEngine(
        IOptionsMonitor<PolicyAsCodeOptions> options,
        IHostEnvironment environment,
        ILogger<JsonPolicyEngine> log)
    {
        this.options = options;
        this.environment = environment;
        this.log = log;
    }

    public PolicyDecision Evaluate(PolicyInput input)
    {
        var opts = options.CurrentValue;
        if (!opts.Enabled)
            return Allow(input, "policy-as-code-disabled", opts.DefaultVersion, "disabled", "disabled", "Policy-as-code disabled.");

        var bundle = LoadBundle(opts);
        foreach (var rule in bundle.Rules.OrderByDescending(x => x.Priority))
        {
            if (!Matches(rule, input)) continue;
            var allow = string.Equals(rule.Effect, "allow", StringComparison.OrdinalIgnoreCase);
            return new PolicyDecision(
                allow,
                allow ? "allow" : "deny",
                bundle.Name,
                bundle.Version,
                cachedDigest,
                rule.Id,
                rule.Reason,
                new Dictionary<string, string>
                {
                    ["subject"] = input.Subject,
                    ["roles"] = string.Join(",", input.Roles),
                    ["action"] = input.Action,
                    ["resource"] = input.Resource,
                    ["risk"] = input.Risk,
                    ["tenantId"] = input.TenantId?.ToString() ?? "",
                    ["matched_rule"] = rule.Id,
                });
        }

        return new PolicyDecision(
            false,
            "deny",
            bundle.Name,
            bundle.Version,
            cachedDigest,
            "default_deny",
            "No policy rule matched; default deny.",
            new Dictionary<string, string>
            {
                ["subject"] = input.Subject,
                ["roles"] = string.Join(",", input.Roles),
                ["action"] = input.Action,
                ["resource"] = input.Resource,
                ["risk"] = input.Risk,
                ["tenantId"] = input.TenantId?.ToString() ?? "",
            });
    }

    private PolicyBundle LoadBundle(PolicyAsCodeOptions opts)
    {
        if (cached is not null) return cached;

        var path = ResolvePath(opts.BundlePath);
        if (!File.Exists(path))
        {
            if (environment.IsProduction())
                throw new InvalidOperationException($"Policy bundle not found: {path}");
            cachedDigest = "missing-dev-bundle";
            return cached = PolicyBundle.Default(opts.DefaultVersion);
        }

        var json = File.ReadAllText(path);
        cachedDigest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
        if (opts.RequireSignedBundle)
            VerifySignature(opts, json);

        cached = JsonSerializer.Deserialize<PolicyBundle>(json, JsonOptions)
            ?? PolicyBundle.Default(opts.DefaultVersion);
        log.LogInformation("Loaded policy bundle {Name} {Version} digest={Digest}", cached.Name, cached.Version, cachedDigest);
        return cached;
    }

    private void VerifySignature(PolicyAsCodeOptions opts, string json)
    {
        var sigPath = ResolvePath(opts.BundleSignaturePath);
        if (!File.Exists(sigPath))
        {
            if (environment.IsProduction())
                throw new InvalidOperationException($"Policy bundle signature missing: {sigPath}");
            return;
        }

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(opts.SigningSecret));
        var expected = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
        var actual = File.ReadAllText(sigPath).Trim().ToLowerInvariant();
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(actual)))
            throw new InvalidOperationException("Policy bundle signature verification failed.");
    }

    private string ResolvePath(string path)
        => Path.IsPathRooted(path) ? path : Path.Combine(AppContext.BaseDirectory, path);

    private static bool Matches(PolicyRule rule, PolicyInput input)
    {
        if (!MatchesAny(rule.Actions, input.Action)) return false;
        if (!MatchesAny(rule.Resources, input.Resource)) return false;
        if (rule.Risks.Length > 0 && !rule.Risks.Contains(input.Risk, StringComparer.OrdinalIgnoreCase)) return false;
        if (rule.Roles.Length > 0 && !input.Roles.Any(r => rule.Roles.Contains(r, StringComparer.OrdinalIgnoreCase))) return false;
        if (rule.RequireTenant && input.TenantId is null) return false;
        return true;
    }

    private static bool MatchesAny(string[] patterns, string value)
    {
        if (patterns.Length == 0) return true;
        return patterns.Any(pattern =>
            pattern == "*"
            || string.Equals(pattern, value, StringComparison.OrdinalIgnoreCase)
            || (pattern.EndsWith('*') && value.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase)));
    }

    private static PolicyDecision Allow(PolicyInput input, string rule, string version, string policy, string digest, string reason)
        => new(true, "allow", policy, version, digest, rule, reason, new Dictionary<string, string>
        {
            ["action"] = input.Action,
            ["resource"] = input.Resource,
        });

    private sealed record PolicyBundle(string Name, string Version, PolicyRule[] Rules)
    {
        public static PolicyBundle Default(string version) => new("default-dev-policy", version,
        [
            new("dev_allow_read", "allow", 10, ["*"], ["*"], [], [], false, "Development default allow."),
        ]);
    }

    private sealed record PolicyRule(
        string Id,
        string Effect,
        int Priority,
        string[] Actions,
        string[] Resources,
        string[] Roles,
        string[] Risks,
        bool RequireTenant,
        string Reason);
}
