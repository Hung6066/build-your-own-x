using Hope.Agent.Application.Security;
using Hope.Agent.Domain.Security;
using Microsoft.Extensions.Options;

namespace Hope.Agent.Infrastructure.Security;

internal sealed class ConfigurableToolApprovalPolicy(IOptionsMonitor<ToolApprovalOptions> opts) : IToolApprovalPolicy
{
    public ApprovalPolicyDecision Decide(string toolName, string argumentsJson)
    {
        var o = opts.CurrentValue;
        if (!o.Enabled)
        {
            return new ApprovalPolicyDecision(ApprovalDecisionKind.AutoApprove, ToolImpactLevel.ReadOnly, "policy_disabled");
        }

        var impact = o.Tools.TryGetValue(toolName, out var mapped) ? mapped : o.DefaultImpact;
        var kind = impact switch
        {
            ToolImpactLevel.ReadOnly => ApprovalDecisionKind.AutoApprove,
            ToolImpactLevel.Write => ApprovalDecisionKind.RequireApproval,
            ToolImpactLevel.Critical => ApprovalDecisionKind.RequireApproval,
            _ => ApprovalDecisionKind.RequireApproval,
        };
        return new ApprovalPolicyDecision(kind, impact);
    }
}
