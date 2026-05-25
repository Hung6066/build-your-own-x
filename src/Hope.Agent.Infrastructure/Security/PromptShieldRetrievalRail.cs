using Hope.Agent.Application.Abstractions;
using Hope.Agent.Application.Observability;
using Hope.Agent.Application.Security;
using Microsoft.Extensions.Logging;

namespace Hope.Agent.Infrastructure.Security;

/// <summary>
/// Retrieval rail that delegates injection detection to the existing
/// <see cref="IPromptShield"/> (HeuristicPromptShield).
///
/// Each memory chunk's content is passed through the shield independently.
/// Chunks that trigger the shield are dropped and a warning is logged so
/// operators can locate and remove poisoned records from the knowledge base.
/// </summary>
internal sealed class PromptShieldRetrievalRail(
    IPromptShield shield,
    ILogger<PromptShieldRetrievalRail> log) : IRetrievalRail
{
    public IReadOnlyList<MemorySearchHit> Filter(IReadOnlyList<MemorySearchHit> hits)
    {
        if (hits.Count == 0) return hits;

        var safe = new List<MemorySearchHit>(hits.Count);
        foreach (var hit in hits)
        {
            var result = shield.Inspect(hit.Record.Content);
            if (result.Allowed)
            {
                safe.Add(hit);
            }
            else
            {
                log.LogWarning(
                    "RetrievalRail: dropped memory chunk {RecordId} (kind={Kind}, score={Score:F3}) " +
                    "— injection pattern detected: [{Reasons}]",
                    hit.Record.Id, hit.Record.Kind, hit.Score, string.Join(", ", result.Reasons));
                HopeMeters.PromptShieldBlocks.Add(1,
                    new KeyValuePair<string, object?>("source", "retrieval_rail"),
                    new KeyValuePair<string, object?>("kind", hit.Record.Kind.ToString()));
            }
        }

        if (safe.Count < hits.Count)
        {
            log.LogInformation(
                "RetrievalRail: {Dropped}/{Total} memory chunks dropped due to injection patterns.",
                hits.Count - safe.Count, hits.Count);
        }

        return safe;
    }
}
