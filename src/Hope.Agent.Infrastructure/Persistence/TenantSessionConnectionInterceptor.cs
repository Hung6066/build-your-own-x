using System.Data.Common;
using Hope.Agent.Application.Security;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Hope.Agent.Infrastructure.Persistence;

internal sealed class TenantSessionConnectionInterceptor(ITenantContext tenantContext) : DbConnectionInterceptor
{
    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        SetTenant(connection, tenantContext.TenantId);
        base.ConnectionOpened(connection, eventData);
    }

    public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        await SetTenantAsync(connection, tenantContext.TenantId, cancellationToken).ConfigureAwait(false);
        await base.ConnectionOpenedAsync(connection, eventData, cancellationToken).ConfigureAwait(false);
    }

    private static void SetTenant(DbConnection connection, Guid tenantId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT set_config('app.tenant_id', @tenant_id, false)";
        var p = cmd.CreateParameter();
        p.ParameterName = "tenant_id";
        p.Value = tenantId.ToString();
        cmd.Parameters.Add(p);
        cmd.ExecuteNonQuery();
    }

    private static async Task SetTenantAsync(DbConnection connection, Guid tenantId, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT set_config('app.tenant_id', @tenant_id, false)";
        var p = cmd.CreateParameter();
        p.ParameterName = "tenant_id";
        p.Value = tenantId.ToString();
        cmd.Parameters.Add(p);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
