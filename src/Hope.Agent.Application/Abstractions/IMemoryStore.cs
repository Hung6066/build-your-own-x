using Hope.Agent.Domain.Memory;

namespace Hope.Agent.Application.Abstractions;

public interface IMemoryStore
{
    Task UpsertAsync(MemoryRecord record, ReadOnlyMemory<float> embedding, CancellationToken ct);
    Task<IReadOnlyList<MemorySearchHit>> SearchAsync(Guid userId, ReadOnlyMemory<float> query, int topK, MemoryKind? kind, CancellationToken ct);
    /// <summary>
    /// Hybrid retrieval combining dense (semantic) and sparse (lexical/BM25) candidates fused with
    /// Reciprocal Rank Fusion (RRF) on the Qdrant side, then re-ranked by importance × recency decay.
    /// Improves recall on exact tokens (drug names, ICD codes, patient identifiers) that pure dense
    /// search misses.
    /// </summary>
    Task<IReadOnlyList<MemorySearchHit>> SearchHybridAsync(Guid userId, ReadOnlyMemory<float> dense, string queryText, int topK, MemoryKind? kind, CancellationToken ct);
    /// <summary>Returns up to 1 memory whose raw cosine similarity exceeds <paramref name="threshold"/>.
    /// Used for deduplication before inserting a new episodic record.</summary>
    Task<IReadOnlyList<MemorySearchHit>> FindSimilarAsync(Guid userId, ReadOnlyMemory<float> query, float threshold, CancellationToken ct);
    /// <summary>Increases the stored importance of an existing memory by <paramref name="delta"/>, capped at 1.0.</summary>
    Task BumpImportanceAsync(Guid memoryId, float delta, CancellationToken ct);
    /// <summary>Permanently removes a memory. Used by consolidation (superseded facts) and the
    /// forgetting/maintenance job. Missing ids are ignored.</summary>
    Task DeleteAsync(Guid memoryId, CancellationToken ct);
}

public sealed record MemorySearchHit(MemoryRecord Record, float Score);
