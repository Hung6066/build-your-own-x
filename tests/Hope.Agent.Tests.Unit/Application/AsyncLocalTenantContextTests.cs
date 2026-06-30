using FluentAssertions;
using Hope.Agent.Application.Security;
using Xunit;

namespace Hope.Agent.Tests.Unit.Application;

public sealed class AsyncLocalTenantContextTests
{
    [Fact]
    public void Default_tenant_is_returned_when_no_scope_active()
    {
        var ctx = new AsyncLocalTenantContext();

        ctx.TenantId.Should().Be(SecurityDefaults.DefaultTenantId);
    }

    [Fact]
    public void Use_sets_tenant_within_scope_and_restores_on_dispose()
    {
        var ctx = new AsyncLocalTenantContext();
        var tenant = Guid.NewGuid();

        using (ctx.Use(tenant))
        {
            ctx.TenantId.Should().Be(tenant);
        }

        ctx.TenantId.Should().Be(SecurityDefaults.DefaultTenantId);
    }

    [Fact]
    public void Nested_scopes_restore_previous_tenant()
    {
        var ctx = new AsyncLocalTenantContext();
        var outer = Guid.NewGuid();
        var inner = Guid.NewGuid();

        using (ctx.Use(outer))
        {
            using (ctx.Use(inner))
            {
                ctx.TenantId.Should().Be(inner);
            }

            ctx.TenantId.Should().Be(outer);
        }
    }

    [Fact]
    public async Task Tenant_flows_with_async_context_and_isolates_parallel_flows()
    {
        var ctx = new AsyncLocalTenantContext();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        var taskA = Task.Run(async () =>
        {
            using (ctx.Use(tenantA))
            {
                await Task.Delay(20);
                return ctx.TenantId;
            }
        });
        var taskB = Task.Run(async () =>
        {
            using (ctx.Use(tenantB))
            {
                await Task.Delay(20);
                return ctx.TenantId;
            }
        });

        (await taskA).Should().Be(tenantA);
        (await taskB).Should().Be(tenantB);
    }
}
