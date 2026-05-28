using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;

namespace Hope.Agent.Api.Security;

/// <summary>
/// Authorization requirement — caller must hold the <c>tenant</c> claim and it
/// must match the resource's tenant. Closes the multi-tenant isolation gap (C5)
/// where a clinician at hospital A could query memories from hospital B.
/// </summary>
public sealed class TenantRequirement : IAuthorizationRequirement
{
    public string RouteOrHeaderName { get; }
    public TenantRequirement(string routeOrHeaderName = "tenantId")
    {
        RouteOrHeaderName = routeOrHeaderName;
    }
}

internal sealed class TenantHandler(
    IHttpContextAccessor http,
    ILoggerFactory loggers) : AuthorizationHandler<TenantRequirement>
{
    private readonly ILogger _log = loggers.CreateLogger("Hope.Agent.Auth");

    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context, TenantRequirement requirement)
    {
        var user = context.User;
        if (user.Identity?.IsAuthenticated != true)
            return Task.CompletedTask;

        if (user.IsInRole("admin") || user.IsInRole("system"))
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        var callerTenant = user.FindFirstValue("tenant");
        if (string.IsNullOrWhiteSpace(callerTenant))
        {
            // Caller has no tenant claim → cannot prove membership.
            return Task.CompletedTask;
        }

        var ctx = http.HttpContext;
        if (ctx is null) return Task.CompletedTask;

        // Resolve the requested tenant: route → query → header X-Tenant-Id.
        var requested =
            (ctx.Request.RouteValues.TryGetValue(requirement.RouteOrHeaderName, out var rv) ? rv?.ToString() : null)
            ?? ctx.Request.Query[requirement.RouteOrHeaderName].ToString()
            ?? ctx.Request.Headers["X-Tenant-Id"].ToString();

        // No requested tenant → caller implicitly operates within their own tenant.
        if (string.IsNullOrWhiteSpace(requested))
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        if (string.Equals(callerTenant, requested, StringComparison.Ordinal))
        {
            context.Succeed(requirement);
        }
        else
        {
            _log.LogWarning(
                "authz.tenant.denied | caller={Caller} requested={Requested} ip={Ip} path={Path}",
                callerTenant,
                requested,
                ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                ctx.Request.Path.Value);
        }

        return Task.CompletedTask;
    }
}
