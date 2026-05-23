using Hope.Agent.Application.LLM;
using Hope.Agent.Application.Rag;
using Microsoft.Extensions.Options;

namespace Hope.Agent.Rag.Retrieval;

internal sealed class HybridRetriever(
    IVectorIndex index,
    ILLMRouter llm,
    IReranker reranker,
    IOptions<RagOptions> opts) : IRetriever
{
    private readonly RagOptions _opts = opts.Value;

    public async Task<IReadOnlyList<RetrievalHit>> SearchAsync(RetrievalQuery query, CancellationToken ct)
    {
        var embedder = llm.SelectEmbedding();
        var qVec = (await embedder.EmbedAsync(new EmbeddingRequest([query.Query]), ct)).Vectors[0];

        var raw = await index.SearchAsync(query.Collection, qVec, query.TopK, query.MetadataFilter, ct);
        var candidates = raw.Select(h => new RetrievalHit(
            DocumentId: Guid.TryParse(h.Payload.GetValueOrDefault("document_id", string.Empty), out var d) ? d : Guid.Empty,
            ChunkId: h.Id,
            Title: h.Payload.GetValueOrDefault("title", string.Empty),
            Content: h.Payload.GetValueOrDefault("content", string.Empty),
            Url: h.Payload.TryGetValue("url", out var u) && !string.IsNullOrEmpty(u) ? u : null,
            Score: h.Score,
            Metadata: h.Payload)).ToList();

        if (candidates.Count == 0) return candidates;

        var shouldRerank = query.Rerank && _opts.RerankByDefault && candidates.Count > query.FinalK;
        if (!shouldRerank) return candidates.Take(query.FinalK).ToList();

        return await reranker.RerankAsync(query.Query, candidates, query.FinalK, ct);
    }
}
