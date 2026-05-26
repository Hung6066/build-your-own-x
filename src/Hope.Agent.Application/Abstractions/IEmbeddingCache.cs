namespace Hope.Agent.Application.Abstractions;

/// <summary>
/// Redis-backed cache for embedding vectors. Keyed by input text hash.
/// Prevents redundant embedding API calls under high concurrent-agent workloads.
/// </summary>
public interface IEmbeddingCache
{
    /// <summary>Returns a cached vector for the given text, or null on miss.</summary>
    ValueTask<ReadOnlyMemory<float>?> GetAsync(string text, CancellationToken ct);

    /// <summary>Stores a vector in the cache with configured TTL.</summary>
    ValueTask SetAsync(string text, ReadOnlyMemory<float> vector, CancellationToken ct);
}
