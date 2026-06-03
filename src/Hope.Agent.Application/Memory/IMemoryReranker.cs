using Hope.Agent.Application.Abstractions;

namespace Hope.Agent.Application.Memory;

/// <summary>
/// Re-orders retrieved memory candidates by deep relevance to the current query, lifting precision of
/// the final top-K beyond what the vector/fusion score alone achieves. Implementations must be
/// fail-open: on any error they return the input candidates unchanged.
/// </summary>
public interface IMemoryReranker
{
    Task<IReadOnlyList<MemorySearchHit>> RerankAsync(
        string query,
        IReadOnlyList<MemorySearchHit> candidates,
        int topK,
        CancellationToken ct);
}
