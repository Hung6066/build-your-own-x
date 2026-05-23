using Hope.Agent.Domain.Memory;

namespace Hope.Agent.Application.Abstractions;

public interface IMemoryStore
{
    Task UpsertAsync(MemoryRecord record, ReadOnlyMemory<float> embedding, CancellationToken ct);
    Task<IReadOnlyList<MemorySearchHit>> SearchAsync(Guid userId, ReadOnlyMemory<float> query, int topK, MemoryKind? kind, CancellationToken ct);
}

public sealed record MemorySearchHit(MemoryRecord Record, float Score);
