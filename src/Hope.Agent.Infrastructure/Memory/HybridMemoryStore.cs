using Hope.Agent.Application.Abstractions;
using Hope.Agent.Domain.Memory;
using Microsoft.Extensions.Logging;

namespace Hope.Agent.Infrastructure.Memory;

internal sealed class HybridMemoryStore(
    EfMemoryStore postgres,
    QdrantMemoryStore qdrant,
    ILogger<HybridMemoryStore> log) : IMemoryStore
{
    public async Task UpsertAsync(MemoryRecord record, ReadOnlyMemory<float> embedding, CancellationToken ct)
    {
        await postgres.UpsertAsync(record, embedding, ct).ConfigureAwait(false);

        try
        {
            await qdrant.UpsertAsync(record, embedding, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Qdrant memory index upsert failed after Postgres mirror write. MemoryId={MemoryId}", record.Id);
        }
    }

    public async Task<IReadOnlyList<MemorySearchHit>> SearchAsync(
        Guid userId,
        ReadOnlyMemory<float> query,
        int topK,
        MemoryKind? kind,
        CancellationToken ct)
    {
        try
        {
            return await qdrant.SearchAsync(userId, query, topK, kind, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Qdrant memory search failed; falling back to Postgres recent memories. UserId={UserId}", userId);
            return await postgres.SearchAsync(userId, query, topK, kind, ct).ConfigureAwait(false);
        }
    }

    public async Task<IReadOnlyList<MemorySearchHit>> SearchHybridAsync(
        Guid userId,
        ReadOnlyMemory<float> dense,
        string queryText,
        int topK,
        MemoryKind? kind,
        CancellationToken ct)
    {
        try
        {
            return await qdrant.SearchHybridAsync(userId, dense, queryText, topK, kind, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Qdrant hybrid memory search failed; falling back to Postgres recent memories. UserId={UserId}", userId);
            return await postgres.SearchHybridAsync(userId, dense, queryText, topK, kind, ct).ConfigureAwait(false);
        }
    }

    public async Task<IReadOnlyList<MemorySearchHit>> FindSimilarAsync(
        Guid userId,
        ReadOnlyMemory<float> query,
        float threshold,
        CancellationToken ct)
    {
        try
        {
            return await qdrant.FindSimilarAsync(userId, query, threshold, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Qdrant similar-memory lookup failed; deduplication will be skipped. UserId={UserId}", userId);
            return await postgres.FindSimilarAsync(userId, query, threshold, ct).ConfigureAwait(false);
        }
    }

    public async Task BumpImportanceAsync(Guid memoryId, float delta, CancellationToken ct)
    {
        await postgres.BumpImportanceAsync(memoryId, delta, ct).ConfigureAwait(false);

        try
        {
            await qdrant.BumpImportanceAsync(memoryId, delta, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Qdrant memory importance update failed after Postgres mirror update. MemoryId={MemoryId}", memoryId);
        }
    }

    public async Task DeleteAsync(Guid memoryId, CancellationToken ct)
    {
        await postgres.DeleteAsync(memoryId, ct).ConfigureAwait(false);

        try
        {
            await qdrant.DeleteAsync(memoryId, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Qdrant memory delete failed after Postgres mirror delete. MemoryId={MemoryId}", memoryId);
        }
    }
}
