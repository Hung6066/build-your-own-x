using System.Security.Claims;
using Hope.Agent.Application.Security;

namespace Hope.Agent.Api.Middleware;

/// <summary>
/// Resolves the tenant for the current request. The JWT "tenant" claim is the
/// source of truth; the X-Tenant-Id header is only honored for unauthenticated
/// requests. A header that contradicts the authenticated claim is rejected (403)
/// to close the cross-tenant override vector.
/// Must run AFTER UseAuthentication() so claims are populated.
/// </summary>
public sealed class TenantContextMiddleware(RequestDelegate next, ILogger<TenantContextMiddleware> log)
{
    public async Task InvokeAsync(HttpContext context, ITenantContext tenantContext)
    {
        var claimRaw = context.User.FindFirstValue("tenant");
        var headerRaw = context.Request.Headers["X-Tenant-Id"].FirstOrDefault();

        Guid tenantId;
        if (Guid.TryParse(claimRaw, out var claimTenant))
        {
            // Authenticated caller: claim wins. A mismatching header is an attack signal.
            if (!string.IsNullOrWhiteSpace(headerRaw)
                && Guid.TryParse(headerRaw, out var headerTenant)
                && headerTenant != claimTenant)
            {
                log.LogWarning(
                    "Cross-tenant header override rejected: claim tenant {ClaimTenant} != header tenant {HeaderTenant} for {Path}",
                    claimTenant, headerTenant, context.Request.Path);
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new { error = "tenant_mismatch" }).ConfigureAwait(false);
                return;
            }

            tenantId = claimTenant;
        }
        else
        {
            // Unauthenticated (or token without tenant claim): header is advisory only.
            tenantId = Guid.TryParse(headerRaw, out var parsed) ? parsed : SecurityDefaults.DefaultTenantId;
        }

        using (tenantContext.Use(tenantId))
        {
            await next(context).ConfigureAwait(false);
        }
    }
}
