using Hope.Agent.Application.Caching;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Hope.Agent.Infrastructure.Caching;

/// <summary>
/// Redis-backed implementation of IToolResultCache. Replaces NoOpToolResultCache
/// to activate tool result caching (C-4). Keys are user-scoped to prevent
/// cross-tenant cache poisoning. Uses SHA-256 of arguments for deduplication.
/// </summary>
public sealed class RedisToolResultCache : IToolResultCache
{
    private const string KeyPrefix = "toolcache";
    private readonly IDatabase _redis;
    private readonly ILogger<RedisToolResultCache> _log;

    public RedisToolResultCache(IConnectionMultiplexer multiplexer, ILogger<RedisToolResultCache> log)
    {
        _redis = multiplexer.GetDatabase();
        _log = log;
    }

    public async Task<string?> LookupAsync(string toolName, string argsHash, Guid? userId, CancellationToken ct)
    {
        var key = BuildKey(toolName, argsHash, userId);
        var result = await _redis.StringGetAsync(key);
        if (result.IsNull)
        {
            Hope.Agent.Application.Observability.HopeMeters.ToolCacheHits.Add(1,
                new KeyValuePair<string, object?>("tool", toolName));
            return null;
        }

        Hope.Agent.Application.Observability.HopeMeters.ToolCacheHits.Add(1,
            new KeyValuePair<string, object?>("tool", toolName));
        _log.LogDebug("Tool cache hit: {ToolName} for user {UserId}", toolName, userId);
        return result.ToString();
    }

    public async Task StoreAsync(string toolName, string argsHash, Guid? userId, string result, TimeSpan ttl, CancellationToken ct)
    {
        var key = BuildKey(toolName, argsHash, userId);
        await _redis.StringSetAsync(key, result, ttl);
        _log.LogDebug("Tool cache stored: {ToolName} TTL={Ttl} for user {UserId}", toolName, ttl, userId);
    }

    private static RedisKey BuildKey(string toolName, string argsHash, Guid? userId)
        => $"{KeyPrefix}:{toolName}:{userId?.ToString("N") ?? "anon"}:{argsHash}";
}
