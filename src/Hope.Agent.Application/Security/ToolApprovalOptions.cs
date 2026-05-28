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
    /// Max UTF-8 byte size allowed for tool input arguments JSON.
    /// </summary>
    public int SandboxMaxArgumentsBytes { get; set; } = 64 * 1024;

    /// <summary>
    /// Max UTF-8 byte size allowed for tool output returned to the orchestrator.
    /// Oversized output is truncated to this boundary.
    /// </summary>
    public int SandboxMaxOutputBytes { get; set; } = 256 * 1024;

    /// <summary>
    /// Per-tool impact mapping. Keys are tool names (case-insensitive).
    /// </summary>
    public Dictionary<string, ToolImpactLevel> Tools { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Per-tool role allowlists (RBAC).  Keys are tool names (case-insensitive),
    /// values are the roles that may invoke the tool.  An empty array means any
    /// authenticated user may invoke it.  If a tool has no entry, access is open.
    /// <example>
    /// <code>
    /// "ToolApproval": {
    ///   "ToolRoleAccess": {
    ///     "admin_reset_patient": ["physician", "admin"],
    ///     "export_all_records":   ["admin"]
    ///   }
    /// }
    /// </code>
    /// </example>
    /// </summary>
    public Dictionary<string, string[]> ToolRoleAccess { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
