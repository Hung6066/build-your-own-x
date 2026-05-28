using System.Security.Cryptography;
using System.Text;
using Hope.Agent.Application.Security;
using Microsoft.Extensions.Configuration;
using StackExchange.Redis;

namespace Hope.Agent.Infrastructure.Security;

/// <summary>
/// Redis-backed idempotency store implementing the Stripe / Square / AWS pattern.
/// <para>
/// Storage layout: a single string per (userId, key) hashed slot.
/// </para>
/// <list type="bullet">
///   <item><b>Pending</b>: <c>P|{bodyHash}|{unixSec}</c> — TTL 60 s. Set atomically with <c>SET NX</c>.</item>
///   <item><b>Final</b>:   <c>F|{status}|{bodyHash}|{base64Body}</c> — TTL 24 h (default).</item>
/// </list>
/// Keys are SHA-256(userId + ":" + idempotencyKey) so raw client values never appear
/// in Redis (prevents key-enumeration leaking inflight clinical operations).
/// </summary>
internal sealed class RedisIdempotencyStore(
    IConnectionMultiplexer redis,
    IConfiguration cfg) : IIdempotencyStore
{
    // 60 s pending window — handler must complete or crash within this; on crash
    // the record auto-expires so retries are not blocked indefinitely.
    private static readonly TimeSpan PendingTtl = TimeSpan.FromSeconds(60);

    private TimeSpan FinalTtl =>
        TimeSpan.FromHours(cfg.GetValue<int>("Idempotency:RetentionHours", 24));

    public async Task<IdempotencyDecision> TryBeginAsync(
        string key, Guid userId, string requestBodyHash, CancellationToken ct)
    {
        var redisKey = BuildKey(key, userId);
        var db = redis.GetDatabase();
        var nowSec = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var pendingValue = $"P|{requestBodyHash}|{nowSec}";

        // Atomic claim: only succeeds if no record exists.
        var claimed = await db.StringSetAsync(
            redisKey, pendingValue, PendingTtl, when: When.NotExists);
        if (claimed)
            return new IdempotencyDecision.Proceed();

        // Slot already in use — inspect existing value.
        var existing = (string?)await db.StringGetAsync(redisKey);
        if (existing is null)
        {
            // Race: the entry expired between SETNX and GET. Treat as Proceed by
            // claiming again; failure here is benign (returns InProgress on next retry).
            var reclaimed = await db.StringSetAsync(
                redisKey, pendingValue, PendingTtl, when: When.NotExists);
            return reclaimed
                ? new IdempotencyDecision.Proceed()
                : new IdempotencyDecision.InProgress();
        }

        if (existing.StartsWith("P|", StringComparison.Ordinal))
            return new IdempotencyDecision.InProgress();

        if (existing.StartsWith("F|", StringComparison.Ordinal))
        {
            // F|<status>|<bodyHash>|<base64Body>
            var parts = existing.Split('|', 4);
            if (parts.Length < 4) return new IdempotencyDecision.Mismatch();
            var storedHash = parts[2];
            if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(storedHash),
                    Encoding.ASCII.GetBytes(requestBodyHash)))
                return new IdempotencyDecision.Mismatch();
            if (!int.TryParse(parts[1], out var status))
                return new IdempotencyDecision.Mismatch();
            byte[] body;
            try { body = Convert.FromBase64String(parts[3]); }
            catch { return new IdempotencyDecision.Mismatch(); }
            return new IdempotencyDecision.Replay(status, body);
        }

        return new IdempotencyDecision.Mismatch();
    }

    public async Task CompleteAsync(
        string key, Guid userId, int status, string requestBodyHash, byte[] responseBody, CancellationToken ct)
    {
        var redisKey = BuildKey(key, userId);
        var db = redis.GetDatabase();
        // Cap stored response at 256 KB — anything larger is not cached; the slot is
        // simply released so retries re-execute. Prevents Redis memory exhaustion.
        if (responseBody.Length > 256 * 1024)
        {
            await db.KeyDeleteAsync(redisKey);
            return;
        }
        var value = $"F|{status}|{requestBodyHash}|{Convert.ToBase64String(responseBody)}";
        await db.StringSetAsync(redisKey, value, FinalTtl);
    }

    public async Task AbortAsync(string key, Guid userId, CancellationToken ct)
    {
        var db = redis.GetDatabase();
        await db.KeyDeleteAsync(BuildKey(key, userId));
    }

    private static string BuildKey(string clientKey, Guid userId)
    {
        // Namespace per user so two users can independently use the same key value
        // without colliding; hash so raw client keys never appear in Redis.
        var raw = $"{userId:N}:{clientKey}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return $"idem:{Convert.ToHexString(hash).ToLowerInvariant()}";
    }
}
