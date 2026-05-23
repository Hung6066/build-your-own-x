using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Hope.Agent.Application.LLM;
using Hope.Agent.Application.Rag;
using Hope.Agent.Domain.Rag;
using Hope.Agent.Rag.Chunking;
using Hope.Agent.Shared;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hope.Agent.Rag.Ingestion;

internal sealed class IngestionService(
    IDocumentStore docs,
    IVectorIndex index,
    ILLMRouter llm,
    IClock clock,
    IOptions<RagOptions> opts,
    Channel<IngestRequest> channel,
    ILogger<IngestionService> log) : IIngestionService
{
    private readonly RagOptions _opts = opts.Value;

    public async Task<IngestResult> IngestAsync(IngestRequest request, CancellationToken ct)
    {
        var hash = Hash(request.Content);
        var existing = await docs.FindByHashAsync(hash, request.Collection, ct);
        if (existing is not null && existing.Status == DocumentStatus.Ready)
        {
            log.LogInformation("Document already ingested {Id}", existing.Id);
            return new IngestResult(existing.Id, existing.ChunkCount, existing.Status);
        }

        var chunker = new RecursiveTextChunker(_opts.ChunkSize, _opts.ChunkOverlap);
        var pieces = chunker.Split(request.Content);
        if (pieces.Count == 0) throw new InvalidOperationException("Document produced zero chunks");

        var now = clock.UtcNow;
        var documentId = Guid.CreateVersion7();
        var doc = new Document
        {
            Id = documentId,
            Title = request.Title,
            Source = request.Source,
            Collection = request.Collection,
            Url = request.Url,
            ContentHash = hash,
            Status = DocumentStatus.Ingesting,
            ChunkCount = pieces.Count,
            Metadata = request.Metadata ?? [],
            CreatedAt = now,
            UpdatedAt = now,
        };
        var chunkEntities = pieces.Select(p => new DocumentChunk
        {
            Id = Guid.CreateVersion7(),
            DocumentId = documentId,
            Ordinal = p.Ordinal,
            Content = p.Content,
            TokenEstimate = p.TokenEstimate,
            SectionPath = p.SectionPath,
            CreatedAt = now,
        }).ToList();
        await docs.AddDocumentAsync(doc, chunkEntities, ct);

        try
        {
            var embedder = llm.SelectEmbedding();
            var points = new List<VectorPoint>(chunkEntities.Count);
            int dim = 0;
            for (int i = 0; i < chunkEntities.Count; i += _opts.EmbedBatchSize)
            {
                var batch = chunkEntities.Skip(i).Take(_opts.EmbedBatchSize).ToList();
                var emb = await embedder.EmbedAsync(new EmbeddingRequest(batch.Select(c => c.Content).ToList()), ct);
                dim = emb.Vectors[0].Length;
                for (int j = 0; j < batch.Count; j++)
                {
                    var c = batch[j];
                    points.Add(new VectorPoint(c.Id, emb.Vectors[j], new Dictionary<string, string>
                    {
                        ["document_id"] = documentId.ToString(),
                        ["title"] = request.Title,
                        ["source"] = request.Source,
                        ["url"] = request.Url ?? string.Empty,
                        ["section"] = c.SectionPath ?? string.Empty,
                        ["ordinal"] = c.Ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ["content"] = c.Content,
                    }));
                }
            }
            await index.EnsureCollectionAsync(request.Collection, dim, ct);
            await index.UpsertAsync(request.Collection, points, ct);
            await docs.UpdateStatusAsync(documentId, DocumentStatus.Ready, clock.UtcNow, ct);
            log.LogInformation("Ingested {Title} → {Chunks} chunks (collection={Collection})", request.Title, pieces.Count, request.Collection);
            return new IngestResult(documentId, pieces.Count, DocumentStatus.Ready);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Ingestion failed for {Title}", request.Title);
            await docs.UpdateStatusAsync(documentId, DocumentStatus.Failed, clock.UtcNow, ct);
            throw;
        }
    }

    public ValueTask EnqueueAsync(IngestRequest request, CancellationToken ct) =>
        channel.Writer.WriteAsync(request, ct);

    private static string Hash(string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(bytes);
    }
}
