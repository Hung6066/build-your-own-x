using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;

namespace Hope.Agent.Api.Security;

/// <summary>
/// Resolves a <see cref="PatientAccessRequirement"/> against the current
/// <see cref="ClaimsPrincipal"/>. Decision is logged at Warning level when denied
/// so SIEM can alert on broad-BOLA probing.
/// </summary>
internal sealed class PatientAccessHandler(
    IHttpContextAccessor http,
    ILoggerFactory loggers) : AuthorizationHandler<PatientAccessRequirement>
{
    private readonly ILogger _log = loggers.CreateLogger("Hope.Agent.Auth");

    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        PatientAccessRequirement requirement)
    {
        var user = context.User;
        if (user.Identity?.IsAuthenticated != true)
            return Task.CompletedTask;

        // Admin / system can access any patient.
        if (user.IsInRole("admin") || user.IsInRole("system"))
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        var ctx = http.HttpContext;
        if (ctx is null)
            return Task.CompletedTask;

        // 1. Try route value.
        var target = ctx.Request.RouteValues.TryGetValue(requirement.RouteValueName, out var rv)
            ? rv?.ToString()
            : null;
        // 2. Fallback to query string.
        target ??= ctx.Request.Query[requirement.RouteValueName].ToString();

        var subject = user.FindFirstValue(ClaimTypes.NameIdentifier)
                   ?? user.FindFirstValue("sub");

        // No target supplied → handler does not own this decision (let other handlers run
        // or fall back to default endpoint authorization).
        if (string.IsNullOrWhiteSpace(target))
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        // Self-access is always allowed.
        if (string.Equals(target, subject, StringComparison.Ordinal))
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        // Cross-patient access — require explicit grant in the "patients" claim.
        var allowed = user.FindAll("patients")
            .Any(c => c.Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Contains(target, StringComparer.OrdinalIgnoreCase));

        if (allowed)
        {
            context.Succeed(requirement);
        }
        else
        {
            _log.LogWarning(
                "authz.patient.denied | subject={Subject} target={Target} ip={Ip} path={Path}",
                subject,
                target,
                ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                ctx.Request.Path.Value);
            // Do NOT call Fail() — that would force-deny even if another handler succeeds.
            // Leaving the requirement unmet causes Authorization to deny by default.
        }

        return Task.CompletedTask;
    }
}
