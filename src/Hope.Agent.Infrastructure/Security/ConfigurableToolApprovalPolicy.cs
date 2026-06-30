using Hope.Agent.Application.Security;
using Hope.Agent.Application.Observability;
using Hope.Agent.Domain.Security;
using Microsoft.Extensions.Options;

namespace Hope.Agent.Infrastructure.Security;

internal sealed class ConfigurableToolApprovalPolicy(
    IOptionsMonitor<ToolApprovalOptions> opts,
    IPolicyEngine policyEngine) : IToolApprovalPolicy
{
    public ApprovalPolicyDecision Decide(string toolName, string argumentsJson)
    {
        var o = opts.CurrentValue;
        if (!o.Enabled)
        {
            return new ApprovalPolicyDecision(ApprovalDecisionKind.AutoApprove, ToolImpactLevel.ReadOnly, "policy_disabled");
        }

        if (!o.Tools.TryGetValue(toolName, out var mapped))
        {
            HopeMeters.BlockedToolCalls.Add(1, new("tool", toolName), new("reason", "unconfigured"));
            HopeMeters.PolicyDenials.Add(1, new KeyValuePair<string, object?>("rule", "unconfigured_tool_default_deny"));
            return o.AllowUnconfiguredToolAccess
                ? new ApprovalPolicyDecision(ApprovalDecisionKind.RequireApproval, o.DefaultImpact, "unconfigured_tool_requires_review")
                : new ApprovalPolicyDecision(ApprovalDecisionKind.AutoDeny, ToolImpactLevel.Critical, "unconfigured_tool_default_deny");
        }

        var impact = mapped;
        var policyDecision = policyEngine.Evaluate(new PolicyInput(
            Subject: "tool-policy",
            Roles: ["system"],
            Action: $"tool:{toolName}",
            Resource: toolName,
            Risk: impact.ToString(),
            TenantId: null,
            Attributes: new Dictionary<string, string> { ["arguments_hash"] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(argumentsJson ?? "{}")))[..16] }));
        if (!policyDecision.Allow && policyDecision.RuleId != "default_deny")
        {
            HopeMeters.BlockedToolCalls.Add(1, new("tool", toolName), new("reason", "policy_as_code"));
            HopeMeters.PolicyDenials.Add(1, new KeyValuePair<string, object?>("rule", policyDecision.RuleId));
            return new ApprovalPolicyDecision(
                ApprovalDecisionKind.AutoDeny,
                impact,
                $"{policyDecision.PolicyName}/{policyDecision.PolicyVersion}:{policyDecision.RuleId}:{policyDecision.Reason}");
        }

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
