using Hope.Agent.Application.Security;
using Microsoft.Extensions.Options;

namespace Hope.Agent.Infrastructure.Security;

/// <summary>
/// Role-based access control for tool invocations.
/// Configuration-driven: roles per tool are read from <c>ToolApproval:ToolRoleAccess</c>
/// and hot-reloaded via <see cref="IOptionsMonitor{T}"/>.
/// </summary>
internal sealed class ConfigurableToolAccessPolicy(IOptionsMonitor<ToolApprovalOptions> opts) : IToolAccessPolicy
{
    public bool IsAllowed(string toolName, IReadOnlyList<string> userRoles)
    {
        var access = opts.CurrentValue.ToolRoleAccess;
        if (!access.TryGetValue(toolName, out var required) || required.Length == 0)
            return true; // no restriction configured → open access

        return userRoles.Any(r => required.Contains(r, StringComparer.OrdinalIgnoreCase));
    }
}
