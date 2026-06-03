using System.Text;
using System.Text.Json;
using Hope.Agent.Application.Abstractions;
using Hope.Agent.Application.LLM;
using Hope.Agent.Application.Memory;
using Microsoft.Extensions.Logging;

namespace Hope.Agent.Infrastructure.Memory;

/// <summary>
/// LLM-based listwise reranker. Given the query and the fused candidate memories, it scores each
/// candidate's relevance and returns the best <c>topK</c> in order. Lifts precision beyond vector /
/// RRF scores, which are query-agnostic. Fail-open: returns the first <c>topK</c> input candidates
/// on any error or unparsable response.
/// </summary>
internal sealed class LlmMemoryReranker(
    ILLMRouter router,
    ILogger<LlmMemoryReranker> log) : IMemoryReranker
{
    private const string SystemPrompt = """
        You rerank candidate memories by how useful each is for answering the user's current query.
        Score each candidate from 0.0 (irrelevant) to 1.0 (directly relevant). Output STRICT JSON:
        {"ranking":[{"index":<candidate number>,"score":0.0-1.0}]}
        Include every candidate exactly once. Return JSON only, no prose.
        """;

    public async Task<IReadOnlyList<MemorySearchHit>> RerankAsync(
        string query, IReadOnlyList<MemorySearchHit> candidates, int topK, CancellationToken ct)
    {
        if (candidates.Count <= 1) return candidates;

        try
        {
            var prompt = BuildPrompt(query, candidates);
            var chat = router.SelectChat();
            var resp = await chat.CompleteAsync(new ChatRequest(
                Messages: [new("system", SystemPrompt), new("user", prompt)],
                Temperature: 0.0f,
                MaxTokens: 400), ct);

            var scored = ParseRanking(resp.Content, candidates);
            if (scored.Count == 0) return candidates.Take(topK).ToList();

            return scored
                .OrderByDescending(x => x.Score)
                .Take(topK)
                .Select(x => x.Hit)
                .ToList();
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Memory rerank failed; using fusion order");
            return candidates.Take(topK).ToList();
        }
    }

    private static string BuildPrompt(string query, IReadOnlyList<MemorySearchHit> candidates)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"## Query\n{query}\n");
        sb.AppendLine("## Candidates");
        for (var i = 0; i < candidates.Count; i++)
            sb.AppendLine($"[{i}] ({candidates[i].Record.Kind}) {candidates[i].Record.Content}");
        return sb.ToString();
    }

    private static List<(MemorySearchHit Hit, double Score)> ParseRanking(
        string raw, IReadOnlyList<MemorySearchHit> candidates)
    {
        var result = new List<(MemorySearchHit, double)>();
        try
        {
            var json = ExtractJson(raw);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("ranking", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return result;

            var seen = new HashSet<int>();
            foreach (var el in arr.EnumerateArray())
            {
                if (!el.TryGetProperty("index", out var idxEl) || idxEl.ValueKind != JsonValueKind.Number) continue;
                var idx = idxEl.GetInt32();
                if (idx < 0 || idx >= candidates.Count || !seen.Add(idx)) continue;
                var score = el.TryGetProperty("score", out var sc) && sc.ValueKind == JsonValueKind.Number
                    ? sc.GetDouble() : 0.0;
                result.Add((candidates[idx], score));
            }
        }
        catch
        {
            return [];
        }
        return result;
    }

    private static string ExtractJson(string s)
    {
        var start = s.IndexOf('{');
        var end = s.LastIndexOf('}');
        return start >= 0 && end > start ? s.Substring(start, end - start + 1) : s;
    }
}
