namespace Hope.Agent.Application.Caching;

/// <summary>
/// Outcome of a semantic-cache lookup. <see cref="SimilarityScore"/> is the cosine similarity
/// between the live query embedding and the cached entry, in range [0,1].
/// </summary>
public sealed record SemanticCacheHit(
    string Response,
    float SimilarityScore,
    DateTimeOffset CachedAt);

/// <summary>
/// Tenant-scoped semantic cache for chat completions. Reduces LLM spend on repeated
/// or near-duplicate queries by returning a prior response when a fresh embedding
/// is close enough to a previously-served one.
/// <para>
/// Implementations MUST scope cache keys by <c>userId</c> (or another tenancy identifier)
/// to prevent cross-tenant cache poisoning.
/// </para>
/// </summary>
public interface ISemanticChatCache
{
    /// <summary>
    /// Returns the closest cached response whose similarity is &gt;= <paramref name="minSimilarity"/>,
    /// or <c>null</c> if none exists.
    /// </summary>
    Task<SemanticCacheHit?> LookupAsync(
        Guid userId,
        string normalizedQuery,
        ReadOnlyMemory<float> embedding,
        float minSimilarity,
        CancellationToken ct);

    Task StoreAsync(
        Guid userId,
        string normalizedQuery,
        ReadOnlyMemory<float> embedding,
        string response,
        TimeSpan ttl,
        CancellationToken ct);
}

/// <summary>No-op default. Swap to a Redis / Qdrant backed implementation to activate.</summary>
public sealed class NoOpSemanticChatCache : ISemanticChatCache
{
    public Task<SemanticCacheHit?> LookupAsync(
        Guid userId, string normalizedQuery, ReadOnlyMemory<float> embedding,
        float minSimilarity, CancellationToken ct) => Task.FromResult<SemanticCacheHit?>(null);

    public Task StoreAsync(
        Guid userId, string normalizedQuery, ReadOnlyMemory<float> embedding,
        string response, TimeSpan ttl, CancellationToken ct) => Task.CompletedTask;
}
