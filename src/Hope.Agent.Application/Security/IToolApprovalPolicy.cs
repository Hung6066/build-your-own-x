using Hope.Agent.Domain.Security;

namespace Hope.Agent.Application.Security;

public enum ApprovalDecisionKind
{
    AutoApprove = 0,
    RequireApproval = 1,
    AutoDeny = 2,
}

public sealed record ApprovalPolicyDecision(ApprovalDecisionKind Kind, ToolImpactLevel Impact, string? Reason = null);

public interface IToolApprovalPolicy
{
    /// <summary>
    /// Decide whether the given tool invocation requires human approval, can run, or must be denied.
    /// </summary>
    ApprovalPolicyDecision Decide(string toolName, string argumentsJson);
}
