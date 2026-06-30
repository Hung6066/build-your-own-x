using System.Security.Claims;
using FluentAssertions;
using Hope.Agent.Api.Middleware;
using Hope.Agent.Application.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Hope.Agent.Tests.Unit.Api;

public sealed class TenantContextMiddlewareTests
{
    private static DefaultHttpContext BuildContext(Guid? claimTenant, string? headerTenant)
    {
        var ctx = new DefaultHttpContext();
        ctx.Response.Body = new MemoryStream();
        if (claimTenant is { } ct)
        {
            ctx.User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim("tenant", ct.ToString())], authenticationType: "Test"));
        }

        if (headerTenant is not null)
            ctx.Request.Headers["X-Tenant-Id"] = headerTenant;
        return ctx;
    }

    private static (TenantContextMiddleware Middleware, AsyncLocalTenantContext Ctx, TaskCompletionSource<Guid> Observed) Build()
    {
        var observed = new TaskCompletionSource<Guid>();
        var tenantCtx = new AsyncLocalTenantContext();
        var middleware = new TenantContextMiddleware(
            _ =>
            {
                observed.TrySetResult(tenantCtx.TenantId);
                return Task.CompletedTask;
            },
            NullLogger<TenantContextMiddleware>.Instance);
        return (middleware, tenantCtx, observed);
    }

    [Fact]
    public async Task Jwt_tenant_claim_is_source_of_truth()
    {
        var tenant = Guid.NewGuid();
        var (middleware, tenantCtx, observed) = Build();
        var http = BuildContext(tenant, headerTenant: null);

        await middleware.InvokeAsync(http, tenantCtx);

        (await observed.Task).Should().Be(tenant);
    }

    [Fact]
    public async Task Header_matching_claim_is_allowed()
    {
        var tenant = Guid.NewGuid();
        var (middleware, tenantCtx, observed) = Build();
        var http = BuildContext(tenant, tenant.ToString());

        await middleware.InvokeAsync(http, tenantCtx);

        (await observed.Task).Should().Be(tenant);
        http.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
    }

    [Fact]
    public async Task Header_contradicting_claim_is_rejected_with_403()
    {
        var claimTenant = Guid.NewGuid();
        var attackerTenant = Guid.NewGuid();
        var (middleware, tenantCtx, observed) = Build();
        var http = BuildContext(claimTenant, attackerTenant.ToString());

        await middleware.InvokeAsync(http, tenantCtx);

        http.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        observed.Task.IsCompleted.Should().BeFalse("pipeline must short-circuit on tenant mismatch");
    }

    [Fact]
    public async Task Anonymous_request_uses_header_tenant()
    {
        var tenant = Guid.NewGuid();
        var (middleware, tenantCtx, observed) = Build();
        var http = BuildContext(claimTenant: null, tenant.ToString());

        await middleware.InvokeAsync(http, tenantCtx);

        (await observed.Task).Should().Be(tenant);
    }

    [Fact]
    public async Task Anonymous_request_without_header_falls_back_to_default_tenant()
    {
        var (middleware, tenantCtx, observed) = Build();
        var http = BuildContext(claimTenant: null, headerTenant: null);

        await middleware.InvokeAsync(http, tenantCtx);

        (await observed.Task).Should().Be(SecurityDefaults.DefaultTenantId);
    }

    [Fact]
    public async Task Malformed_header_on_anonymous_request_falls_back_to_default_tenant()
    {
        var (middleware, tenantCtx, observed) = Build();
        var http = BuildContext(claimTenant: null, "not-a-guid");

        await middleware.InvokeAsync(http, tenantCtx);

        (await observed.Task).Should().Be(SecurityDefaults.DefaultTenantId);
    }

    [Fact]
    public async Task Malformed_header_with_valid_claim_uses_claim()
    {
        var tenant = Guid.NewGuid();
        var (middleware, tenantCtx, observed) = Build();
        var http = BuildContext(tenant, "garbage");

        await middleware.InvokeAsync(http, tenantCtx);

        (await observed.Task).Should().Be(tenant);
        http.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
    }
}
