using Hope.Agent.Application.Locking;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Hope.Agent.Infrastructure.Locking;

/// <summary>
/// Redis-based distributed lock using SET NX with automatic expiry.
/// Closes gap H-7. Prevents concurrent tool execution across multiple API instances.
/// Uses a unique token (Guid v7) as the lock value so only the holder can release it.
/// </summary>
internal sealed class RedisDistributedLock : IDistributedLock
{
    private readonly IDatabase _redis;
    private readonly ILogger<RedisDistributedLock> _log;
    private const string LockPrefix = "lock";

    public RedisDistributedLock(IConnectionMultiplexer multiplexer, ILogger<RedisDistributedLock> log)
    {
        _redis = multiplexer.GetDatabase();
        _log = log;
    }

    public async Task<ILockHandle?> AcquireAsync(string resource, TimeSpan expiry, CancellationToken ct)
    {
        var key = $"{LockPrefix}:{resource}";
        var token = Guid.CreateVersion7().ToString("N");

        var acquired = await _redis.StringSetAsync(
            key,
            token,
            expiry,
            When.NotExists,
            CommandFlags.DemandMaster);

        if (!acquired)
        {
            _log.LogDebug("Failed to acquire lock: {Resource} (already held)", resource);
            return null;
        }

        _log.LogDebug("Lock acquired: {Resource} token={Token} ttl={Ttl}", resource, token, expiry);
        return new RedisLockHandle(_redis, key, token, resource, _log);
    }

    private sealed class RedisLockHandle : ILockHandle
    {
        private readonly IDatabase _redis;
        private readonly RedisKey _key;
        private readonly ILogger _log;
        private bool _disposed;

        public RedisLockHandle(IDatabase redis, RedisKey key, string token, string resource, ILogger log)
        {
            _redis = redis;
            _key = key;
            _log = log;
            Token = token;
            Resource = resource;
            AcquiredAt = DateTimeOffset.UtcNow;
        }

        public string Token { get; }
        public string Resource { get; }
        public DateTimeOffset AcquiredAt { get; }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;

            // Lua script: only release if token matches (prevents releasing someone else's lock)
            var script = @"if redis.call('GET', KEYS[1]) == ARGV[1] then return redis.call('DEL', KEYS[1]) else return 0 end";
            var result = await _redis.ScriptEvaluateAsync(script, new RedisKey[] { _key }, new RedisValue[] { Token });
            if ((int)result == 1)
                _log.LogDebug("Lock released: {Resource}", Resource);
            else
                _log.LogWarning("Lock release skipped (token mismatch or expired): {Resource}", Resource);
        }
    }
}
