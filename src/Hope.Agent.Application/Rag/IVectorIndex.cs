namespace Hope.Agent.Application.Rag;

public sealed record VectorPoint(
    Guid Id,
    ReadOnlyMemory<float> Embedding,
    Dictionary<string, string> Payload);

public sealed record VectorSearchHit(Guid Id, float Score, Dictionary<string, string> Payload);

public interface IVectorIndex
{
    Task EnsureCollectionAsync(string collection, int dimension, CancellationToken ct);
    Task UpsertAsync(string collection, IReadOnlyList<VectorPoint> points, CancellationToken ct);
    Task<IReadOnlyList<VectorSearchHit>> SearchAsync(
        string collection,
        ReadOnlyMemory<float> query,
        int topK,
        Dictionary<string, string>? mustEqual,
        CancellationToken ct);
    Task DeleteByDocumentAsync(string collection, Guid documentId, CancellationToken ct);
}
