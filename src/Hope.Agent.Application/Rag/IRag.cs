using Hope.Agent.Domain.Rag;

namespace Hope.Agent.Application.Rag;

public sealed record IngestRequest(
    string Title,
    string Content,
    string Collection = "clinical_guidelines",
    string Source = "manual",
    string? Url = null,
    Dictionary<string, string>? Metadata = null);

public sealed record IngestResult(Guid DocumentId, int ChunkCount, DocumentStatus Status);

public sealed record RetrievalQuery(
    string Query,
    string Collection = "clinical_guidelines",
    int TopK = 8,
    int FinalK = 4,
    Dictionary<string, string>? MetadataFilter = null,
    bool Rerank = true);

public sealed record RetrievalHit(
    Guid DocumentId,
    Guid ChunkId,
    string Title,
    string Content,
    string? Url,
    float Score,
    Dictionary<string, string> Metadata);

public interface IDocumentStore
{
    Task<Document?> FindByHashAsync(string contentHash, string collection, CancellationToken ct);
    Task AddDocumentAsync(Document doc, IReadOnlyList<DocumentChunk> chunks, CancellationToken ct);
    Task UpdateStatusAsync(Guid documentId, DocumentStatus status, DateTimeOffset now, CancellationToken ct);
    Task<Document?> GetAsync(Guid documentId, CancellationToken ct);
    Task<IReadOnlyList<DocumentChunk>> GetChunksAsync(Guid documentId, CancellationToken ct);
    Task<IReadOnlyList<DocumentChunk>> GetChunksAsync(IEnumerable<Guid> chunkIds, CancellationToken ct);
}

public interface IIngestionService
{
    Task<IngestResult> IngestAsync(IngestRequest request, CancellationToken ct);
    ValueTask EnqueueAsync(IngestRequest request, CancellationToken ct);
}

public interface IRetriever
{
    Task<IReadOnlyList<RetrievalHit>> SearchAsync(RetrievalQuery query, CancellationToken ct);
}

public interface IReranker
{
    Task<IReadOnlyList<RetrievalHit>> RerankAsync(string query, IReadOnlyList<RetrievalHit> candidates, int finalK, CancellationToken ct);
}
