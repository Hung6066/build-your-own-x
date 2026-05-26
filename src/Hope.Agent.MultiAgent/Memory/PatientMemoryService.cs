using Hope.Agent.Application.Abstractions;
using Hope.Agent.Application.Agents;
using Hope.Agent.Application.LLM;
using Hope.Agent.Domain.Memory;
using Microsoft.Extensions.Logging;

namespace Hope.Agent.MultiAgent.Memory;

/// <summary>
/// Cross-workflow patient memory service backed by <see cref="IMemoryStore"/> (Qdrant).
/// Embeds content via <see cref="IEmbeddingProvider"/> before storing or searching.
/// All errors are swallowed so memory failures never break the critical workflow path.
/// </summary>
internal sealed class PatientMemoryService(
    IMemoryStore memoryStore,
    IEmbeddingProvider embeddings,
    ILogger<PatientMemoryService> log) : IPatientMemoryService
{
    public async Task WriteAsync(
        Guid patientId,
        string content,
        MemoryKind kind = MemoryKind.Clinical,
        float importance = 0.7f,
        CancellationToken ct = default)
    {
        try
        {
            var embResp = await embeddings.EmbedAsync(new EmbeddingRequest([content]), ct);
            var record = new MemoryRecord
            {
                Id = Guid.CreateVersion7(),
                UserId = patientId,
                Kind = kind,
                Content = content,
                Importance = importance,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            await memoryStore.UpsertAsync(record, embResp.Vectors[0], ct);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "PatientMemoryService.WriteAsync failed for patient={PatientId}", patientId);
        }
    }

    public async Task<IReadOnlyList<string>> RetrieveAsync(
        Guid patientId,
        string query,
        int topK = 3,
        MemoryKind? kind = null,
        CancellationToken ct = default)
    {
        try
        {
            var embResp = await embeddings.EmbedAsync(new EmbeddingRequest([query]), ct);
            var hits = await memoryStore.SearchAsync(patientId, embResp.Vectors[0], topK, kind, ct);
            return hits.Select(h => h.Record.Content).ToList();
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "PatientMemoryService.RetrieveAsync failed for patient={PatientId}", patientId);
            return [];
        }
    }
}
