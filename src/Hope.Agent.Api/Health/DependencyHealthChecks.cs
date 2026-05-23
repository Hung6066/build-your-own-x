using Hope.Agent.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;

namespace Hope.Agent.Api.Health;

internal sealed class PostgresHealthCheck(AgentDbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var ok = await db.Database.CanConnectAsync(cancellationToken);
            return ok ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy("Cannot connect");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Postgres unhealthy", ex);
        }
    }
}

internal sealed class RedisHealthCheck(IConnectionMultiplexer redis) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var pong = await redis.GetDatabase().PingAsync();
            return pong.TotalSeconds < 2
                ? HealthCheckResult.Healthy($"ping={pong.TotalMilliseconds:F0}ms")
                : HealthCheckResult.Degraded("slow ping");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Redis unhealthy", ex);
        }
    }
}
