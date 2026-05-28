using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hope.Agent.Application.Security;
using Microsoft.Extensions.Configuration;
using StackExchange.Redis;

namespace Hope.Agent.Infrastructure.Security;

/// <summary>
/// Redis-backed single-use refresh token store.
/// <list type="bullet">
///   <item>Token values are 256-bit URL-safe base64 random strings.</item>
///   <item>Redis keys are SHA-256 hashes of the token — raw tokens are never persisted
///         as keys, preventing exposure through Redis key enumeration commands.</item>
///   <item>Validation and deletion are performed atomically via a Lua script to eliminate
///         the TOCTOU window between GET and DEL that would allow parallel replay attacks.</item>
/// </list>
/// </summary>
internal sealed class RedisRefreshTokenStore(
    IConnectionMultiplexer redis,
    IConfiguration cfg) : IRefreshTokenStore
{
    // Lua: GET token JSON, DEL the token key, also write a short-lived "burned" marker
    // (a tombstone holding only the family pointer) so a later replay can be traced
    // back to the family even though the original key is gone.
    //   KEYS[1] = rt:{hash}            (token key)
    //   KEYS[2] = rt-burned:{hash}     (replay tombstone)
    //   KEYS[3] = rt-fam:{uid}:{fid}   (family member set)
    //   ARGV[1] = burned payload {userId,subject,roles,familyId}
    //   ARGV[2] = TTL seconds for the burned marker
    private const string GetDelBurnLua = """
        local v = redis.call('GET', KEYS[1])
        if v == false then return nil end
        redis.call('DEL', KEYS[1])
        redis.call('SET', KEYS[2], ARGV[1], 'EX', ARGV[2])
        redis.call('SREM', KEYS[3], KEYS[1])
        return v
        """;

    private TimeSpan Ttl =>
        TimeSpan.FromDays(cfg.GetValue<int>("Auth:RefreshTokenLifetimeDays", 7));

    public Task<string> CreateAsync(Guid userId, string subject, string[] roles, CancellationToken ct)
        => CreateInternalAsync(userId, subject, roles, Guid.NewGuid(), ct);

    public Task<string> CreateInFamilyAsync(
        Guid userId, string subject, string[] roles, Guid familyId, CancellationToken ct)
        => CreateInternalAsync(userId, subject, roles, familyId, ct);

    private async Task<string> CreateInternalAsync(
        Guid userId, string subject, string[] roles, Guid familyId, CancellationToken ct)
    {
        // 256-bit URL-safe base64 token (no padding)
        var raw = RandomNumberGenerator.GetBytes(32);
        var token = Convert.ToBase64String(raw)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

        var key = BuildKey(token);
        var famKey = BuildFamilyKey(userId, familyId);
        var payload = JsonSerializer.Serialize(new StoredClaims(userId, subject, roles, familyId));

        var db = redis.GetDatabase();
        var batch = db.CreateBatch();
        var t1 = batch.StringSetAsync(key, payload, Ttl);
        var t2 = batch.SetAddAsync(famKey, key);
        var t3 = batch.KeyExpireAsync(famKey, Ttl);
        batch.Execute();
        await Task.WhenAll(t1, t2, t3);

        return token;
    }

    public async Task<RefreshTokenClaims?> ValidateAndConsumeAsync(string token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token))
            return null;

        var db = redis.GetDatabase();
        var key = BuildKey(token);

        // We need the family id before calling Lua so we can pass the family key.
        // Two-step: peek payload, then run Lua. Race is acceptable because Lua
        // re-reads atomically — peek is only used to construct the family key.
        var raw = (string?)await db.StringGetAsync(key);
        if (raw is null)
            return null;

        StoredClaims? stored;
        try { stored = JsonSerializer.Deserialize<StoredClaims>(raw); }
        catch { return null; }
        if (stored is null) return null;

        var burnedPayload = JsonSerializer.Serialize(stored);
        var famKey = BuildFamilyKey(stored.UserId, stored.FamilyId);

        var result = await db.ScriptEvaluateAsync(
            GetDelBurnLua,
            keys: [new RedisKey(key), new RedisKey(BuildBurnedKey(token)), new RedisKey(famKey)],
            values: [burnedPayload, (RedisValue)(long)Ttl.TotalSeconds]);

        if (result.IsNull) return null;

        return new RefreshTokenClaims(stored.UserId, stored.Subject, stored.Roles, stored.FamilyId);
    }

    public async Task<RefreshTokenClaims?> LookupBurnedAsync(string token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var db = redis.GetDatabase();
        var raw = (string?)await db.StringGetAsync(BuildBurnedKey(token));
        if (raw is null) return null;
        try
        {
            var s = JsonSerializer.Deserialize<StoredClaims>(raw);
            return s is null ? null : new RefreshTokenClaims(s.UserId, s.Subject, s.Roles, s.FamilyId);
        }
        catch { return null; }
    }

    public async Task RevokeFamilyAsync(Guid userId, Guid familyId, CancellationToken ct)
    {
        var db = redis.GetDatabase();
        var famKey = BuildFamilyKey(userId, familyId);
        var members = await db.SetMembersAsync(famKey);
        if (members.Length > 0)
        {
            var keys = new RedisKey[members.Length];
            for (var i = 0; i < members.Length; i++) keys[i] = (string)members[i]!;
            await db.KeyDeleteAsync(keys);
        }
        await db.KeyDeleteAsync(famKey);
    }

    public async Task RevokeAsync(string token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token)) return;
        var db = redis.GetDatabase();
        await db.KeyDeleteAsync(BuildKey(token));
    }

    // Hash the raw token before using it as a Redis key.
    private static string BuildKey(string token)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return $"rt:{Convert.ToHexString(hash).ToLowerInvariant()}";
    }

    private static string BuildBurnedKey(string token)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return $"rt-burned:{Convert.ToHexString(hash).ToLowerInvariant()}";
    }

    private static string BuildFamilyKey(Guid userId, Guid familyId)
        => $"rt-fam:{userId:N}:{familyId:N}";

    private sealed record StoredClaims(Guid UserId, string Subject, string[] Roles, Guid FamilyId);
}
