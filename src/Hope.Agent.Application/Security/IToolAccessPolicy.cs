namespace Hope.Agent.Application.Security;

/// <summary>
/// Decides whether the current user (identified by their roles) is permitted to invoke a tool.
/// Enforces RBAC at the tool level — critical in healthcare where some tools should be
/// restricted to specific roles (e.g., "physician", "admin") regardless of LLM intent.
/// Addresses OWASP LLM08 — Excessive Agency.
/// </summary>
public interface IToolAccessPolicy
{
    /// <summary>
    /// Returns <c>true</c> if <paramref name="userRoles"/> include at least one of the roles
    /// required to invoke <paramref name="toolName"/>.
    /// Returns <c>true</c> when no role restriction is configured for the tool (open access).
    /// </summary>
    bool IsAllowed(string toolName, IReadOnlyList<string> userRoles);
}
