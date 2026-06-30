using Hope.Agent.Application.Security;
using Hope.Agent.Application.Observability;
using Microsoft.Extensions.Options;

namespace Hope.Agent.Infrastructure.Security;

/// <summary>
/// Role-based access control for tool invocations.
/// Configuration-driven: roles per tool are read from <c>ToolApproval:ToolRoleAccess</c>
/// and hot-reloaded via <see cref="IOptionsMonitor{T}"/>.
/// </summary>
internal sealed class ConfigurableToolAccessPolicy(
    IOptionsMonitor<ToolApprovalOptions> opts,
    IPolicyEngine policyEngine) : IToolAccessPolicy
{
    public bool IsAllowed(string toolName, IReadOnlyList<string> userRoles)
    {
        var access = opts.CurrentValue.ToolRoleAccess;
        if (!access.TryGetValue(toolName, out var required) || required.Length == 0)
            return opts.CurrentValue.AllowUnconfiguredToolAccess;

        var decision = policyEngine.Evaluate(new PolicyInput(
            Subject: "tool-access",
            Roles: userRoles,
            Action: $"tool:{toolName}",
            Resource: toolName,
            Risk: opts.CurrentValue.Tools.TryGetValue(toolName, out var impact) ? impact.ToString() : "unknown",
            TenantId: null));
        if (!decision.Allow && decision.RuleId != "default_deny")
        {
            HopeMeters.BlockedToolCalls.Add(1, new("tool", toolName), new("reason", "access_policy"));
            HopeMeters.PolicyDenials.Add(1, new KeyValuePair<string, object?>("rule", decision.RuleId));
            return false;
        }

        return userRoles.Any(r => required.Contains(r, StringComparer.OrdinalIgnoreCase));
    }
}
