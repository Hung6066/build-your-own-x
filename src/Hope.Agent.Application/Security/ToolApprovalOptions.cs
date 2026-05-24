using Hope.Agent.Domain.Security;

namespace Hope.Agent.Application.Security;

public sealed class ToolApprovalOptions
{
    public const string Section = "ToolApproval";

    /// <summary>
    /// Master switch. When false, all tools are auto-approved (legacy behavior).
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Default impact for tools not listed in <see cref="Tools"/>.
    /// </summary>
    public ToolImpactLevel DefaultImpact { get; set; } = ToolImpactLevel.ReadOnly;

    /// <summary>
    /// Seconds to wait for a human decision before default-denying.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Max wall-clock duration (ms) a single tool invocation may run under the sandbox.
    /// </summary>
    public int SandboxToolTimeoutMs { get; set; } = 30_000;

    /// <summary>
    /// Per-tool impact mapping. Keys are tool names (case-insensitive).
    /// </summary>
    public Dictionary<string, ToolImpactLevel> Tools { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
