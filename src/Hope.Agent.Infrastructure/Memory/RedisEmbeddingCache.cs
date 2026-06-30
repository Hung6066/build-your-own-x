using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Hope.Agent.Application.Abstractions;
using Hope.Agent.Application.Security;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Hope.Agent.Infrastructure.Memory;

public sealed class EmbeddingCacheOptions
{
    public const string Section = "EmbeddingCache";

    /// <summary>Cache TTL for embedding vectors. Default 60 minutes.</summary>
    public int TtlMinutes { get; set; } = 60;

    /// <summary>Set false to bypass the cache entirely (e.g. for testing).</summary>
    public bool Enabled { get; set; } = true;
}

/// <summary>
/// Redis-backed embedding cache. Key = "emb:v1:{first-32-hex-chars-of-SHA256(text)}".
/// Serialises float[] as raw little-endian bytes for maximum read/write speed.
/// Thread-safe: IConnectionMultiplexer is itself thread-safe (StackExchange.Redis guarantee).
/// </summary>
internal sealed class RedisEmbeddingCache(
    IConnectionMultiplexer redis,
    IOptions<EmbeddingCacheOptions> opts,
    IOptionsMonitor<DataPerimeterOptions> perimeter) : IEmbeddingCache
{
    private string CacheKey(string text)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(text), hash);
        var prefix = string.IsNullOrWhiteSpace(perimeter.CurrentValue.RedisKeyPrefix) ? "hope" : perimeter.CurrentValue.RedisKeyPrefix.Trim(':');
        return $"{prefix}:emb:v1:{Convert.ToHexString(hash)[..32]}";
    }

    public async ValueTask<ReadOnlyMemory<float>?> GetAsync(string text, CancellationToken ct)
    {
        if (!opts.Value.Enabled) return null;

        var db = redis.GetDatabase();
        var val = await db.StringGetAsync(CacheKey(text)).WaitAsync(ct);
        if (val.IsNullOrEmpty) return null;

        var bytes = (byte[])val!;
        if (bytes.Length % sizeof(float) != 0) return null; // corrupt entry

        var floats = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, floats, 0, bytes.Length);
        return floats;
    }

    public async ValueTask SetAsync(string text, ReadOnlyMemory<float> vector, CancellationToken ct)
    {
        if (!opts.Value.Enabled) return;

        var bytes = new byte[vector.Length * sizeof(float)];
        MemoryMarshal.AsBytes(vector.Span).CopyTo(bytes);

        var db = redis.GetDatabase();
        await db.StringSetAsync(CacheKey(text), bytes, TimeSpan.FromMinutes(opts.Value.TtlMinutes))
                .WaitAsync(ct);
    }
}
