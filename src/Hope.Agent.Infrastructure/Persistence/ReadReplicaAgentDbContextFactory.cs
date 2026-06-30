using Hope.Agent.Application.Governance;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Hope.Agent.Infrastructure.Persistence;

public interface IReadOnlyAgentDbContextFactory
{
    Task<AgentDbContext> CreateAsync(CancellationToken ct = default);
}

internal sealed class ReadReplicaAgentDbContextFactory(
    IConfiguration configuration,
    IOptionsMonitor<DatabaseScaleOptions> databaseScale,
    IOptionsMonitor<RuntimeScaleOptions> runtimeScale,
    TenantSessionConnectionInterceptor tenantInterceptor) : IReadOnlyAgentDbContextFactory
{
    public Task<AgentDbContext> CreateAsync(CancellationToken ct = default)
    {
        var useReplica = databaseScale.CurrentValue.PreferReadReplicaForDashboard;
        var replicaConnectionName = string.IsNullOrWhiteSpace(runtimeScale.CurrentValue.ReadReplicaConnectionName)
            ? "PostgresReadReplica"
            : runtimeScale.CurrentValue.ReadReplicaConnectionName;

        var connStr = useReplica
            ? configuration.GetConnectionString(replicaConnectionName)
            : null;

        if (string.IsNullOrWhiteSpace(connStr))
            connStr = configuration.GetConnectionString("Postgres")
                ?? throw new InvalidOperationException("Missing Postgres connection string.");

        var options = new DbContextOptionsBuilder<AgentDbContext>()
            .UseNpgsql(connStr, npg => npg.EnableRetryOnFailure(3))
            .AddInterceptors(tenantInterceptor)
            .Options;

        return Task.FromResult(new AgentDbContext(options));
    }
}