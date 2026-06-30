using Hope.Agent.Application.Abstractions;
using Hope.Agent.Application.Security;
using Hope.Agent.Domain.Memory;
using Hope.Agent.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Hope.Agent.Infrastructure.Memory;

internal sealed class EfMemoryStore(IDbContextFactory<AgentDbContext> dbFactory) : IMemoryStore
{
    public async Task UpsertAsync(MemoryRecord record, ReadOnlyMemory<float> embedding, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        if (record.TenantId is null)
        {
            record = new MemoryRecord
            {
                Id = record.Id,
                TenantId = SecurityDefaults.DefaultTenantId,
                UserId = record.UserId,
                ConversationId = record.ConversationId,
                Kind = record.Kind,
                Content = record.Content,
                Source = record.Source,
                Importance = record.Importance,
                Metadata = record.Metadata,
                CreatedAt = record.CreatedAt,
            };
        }

        var exists = await db.Memories.AnyAsync(x => x.Id == record.Id, ct).ConfigureAwait(false);
        if (exists)
        {
            db.Memories.Update(record);
        }
        else
        {
            await db.Memories.AddAsync(record, ct).ConfigureAwait(false);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<MemorySearchHit>> SearchAsync(
        Guid userId,
        ReadOnlyMemory<float> query,
        int topK,
        MemoryKind? kind,
        CancellationToken ct)
        => SearchRecentAsync(userId, topK, kind, ct);

    public Task<IReadOnlyList<MemorySearchHit>> SearchHybridAsync(
        Guid userId,
        ReadOnlyMemory<float> dense,
        string queryText,
        int topK,
        MemoryKind? kind,
        CancellationToken ct)
        => SearchRecentAsync(userId, topK, kind, ct);

    public Task<IReadOnlyList<MemorySearchHit>> FindSimilarAsync(
        Guid userId,
        ReadOnlyMemory<float> query,
        float threshold,
        CancellationToken ct)
        => Task.FromResult<IReadOnlyList<MemorySearchHit>>([]);

    public async Task BumpImportanceAsync(Guid memoryId, float delta, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await db.Memories
            .Where(x => x.Id == memoryId)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(
                    x => x.Importance,
                    x => x.Importance + delta > 1.0f ? 1.0f : x.Importance + delta),
                ct)
            .ConfigureAwait(false);
    }

    public async Task DeleteAsync(Guid memoryId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await db.Memories
            .Where(x => x.Id == memoryId)
            .ExecuteDeleteAsync(ct)
            .ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<MemorySearchHit>> SearchRecentAsync(
        Guid userId,
        int topK,
        MemoryKind? kind,
        CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var query = db.Memories.AsNoTracking().Where(x => x.UserId == userId);
        if (kind is { } k)
            query = query.Where(x => x.Kind == k);

        var records = await query
            .OrderByDescending(x => x.Importance)
            .ThenByDescending(x => x.CreatedAt)
            .Take(Math.Max(topK, 1))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return records.Select(x => new MemorySearchHit(x, x.Importance)).ToList();
    }
}
