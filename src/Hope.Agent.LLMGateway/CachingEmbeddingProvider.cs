using Hope.Agent.Application.Abstractions;
using Hope.Agent.Application.LLM;

namespace Hope.Agent.LLMGateway;

/// <summary>
/// Decorator over <see cref="IEmbeddingProvider"/> that serves cache-hits from Redis.
/// For single-input requests (the common path), one Redis GET replaces a full LLM API round-trip.
/// For multi-input batches, only cache-missing texts are forwarded to the upstream provider.
/// </summary>
internal sealed class CachingEmbeddingProvider(IEmbeddingProvider inner, IEmbeddingCache cache) : IEmbeddingProvider
{
    public string Name => inner.Name;

    public async Task<EmbeddingResponse> EmbedAsync(EmbeddingRequest request, CancellationToken ct)
    {
        if (request.Inputs.Count == 0)
            return await inner.EmbedAsync(request, ct);

        // ── Fast path: single input ──────────────────────────────────────────
        if (request.Inputs.Count == 1)
        {
            var text = request.Inputs[0];
            var hit = await cache.GetAsync(text, ct);
            if (hit is not null)
                return new EmbeddingResponse([hit.Value], inner.Name, "cache", 0);

            var resp = await inner.EmbedAsync(request, ct);
            await cache.SetAsync(text, resp.Vectors[0], ct);
            return resp;
        }

        // ── Batch path: partial cache (check each independently) ─────────────
        var results = new ReadOnlyMemory<float>[request.Inputs.Count];
        var misses = new List<(int Index, string Text)>(request.Inputs.Count);

        for (int i = 0; i < request.Inputs.Count; i++)
        {
            var hit = await cache.GetAsync(request.Inputs[i], ct);
            if (hit is not null)
                results[i] = hit.Value;
            else
                misses.Add((i, request.Inputs[i]));
        }

        if (misses.Count > 0)
        {
            var missResp = await inner.EmbedAsync(
                new EmbeddingRequest(misses.Select(m => m.Text).ToList(), request.Model), ct);

            for (int j = 0; j < misses.Count; j++)
            {
                results[misses[j].Index] = missResp.Vectors[j];
                await cache.SetAsync(misses[j].Text, missResp.Vectors[j], ct);
            }
        }

        return new EmbeddingResponse(results, inner.Name, inner.Name, 0);
    }
}
