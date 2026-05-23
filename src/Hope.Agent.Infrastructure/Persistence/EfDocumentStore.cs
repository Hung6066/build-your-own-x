using Hope.Agent.Application.Rag;
using Hope.Agent.Domain.Rag;
using Microsoft.EntityFrameworkCore;

namespace Hope.Agent.Infrastructure.Persistence;

internal sealed class EfDocumentStore(AgentDbContext db) : IDocumentStore
{
    public async Task<Document?> FindByHashAsync(string contentHash, string collection, CancellationToken ct) =>
        await db.Documents.AsNoTracking().FirstOrDefaultAsync(d => d.ContentHash == contentHash && d.Collection == collection, ct);

    public async Task AddDocumentAsync(Document doc, IReadOnlyList<DocumentChunk> chunks, CancellationToken ct)
    {
        db.Documents.Add(doc);
        db.DocumentChunks.AddRange(chunks);
        await db.SaveChangesAsync(ct);
    }

    public async Task UpdateStatusAsync(Guid documentId, DocumentStatus status, DateTimeOffset now, CancellationToken ct)
    {
        var d = await db.Documents.FirstOrDefaultAsync(x => x.Id == documentId, ct);
        if (d is null) return;
        d.Status = status;
        d.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
    }

    public async Task<Document?> GetAsync(Guid documentId, CancellationToken ct) =>
        await db.Documents.AsNoTracking().FirstOrDefaultAsync(x => x.Id == documentId, ct);

    public async Task<IReadOnlyList<DocumentChunk>> GetChunksAsync(Guid documentId, CancellationToken ct) =>
        await db.DocumentChunks.AsNoTracking().Where(c => c.DocumentId == documentId).OrderBy(c => c.Ordinal).ToListAsync(ct);

    public async Task<IReadOnlyList<DocumentChunk>> GetChunksAsync(IEnumerable<Guid> chunkIds, CancellationToken ct)
    {
        var ids = chunkIds.ToHashSet();
        return await db.DocumentChunks.AsNoTracking().Where(c => ids.Contains(c.Id)).ToListAsync(ct);
    }
}
