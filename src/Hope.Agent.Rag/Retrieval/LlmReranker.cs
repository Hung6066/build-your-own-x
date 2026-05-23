using System.Globalization;
using System.Text;
using System.Text.Json;
using Hope.Agent.Application.LLM;
using Hope.Agent.Application.Rag;

namespace Hope.Agent.Rag.Retrieval;

/// <summary>
/// LLM-as-reranker: asks the chat model to score candidates 0..10 for the query, returns top FinalK.
/// Falls back to original order on any parse failure.
/// </summary>
internal sealed class LlmReranker(ILLMRouter llm) : IReranker
{
    public async Task<IReadOnlyList<RetrievalHit>> RerankAsync(string query, IReadOnlyList<RetrievalHit> candidates, int finalK, CancellationToken ct)
    {
        if (candidates.Count <= finalK) return candidates;

        var sb = new StringBuilder();
        for (int i = 0; i < candidates.Count; i++)
        {
            var snippet = candidates[i].Content;
            if (snippet.Length > 600) snippet = snippet[..600];
            sb.Append('[').Append(i).Append("] ").Append(snippet).Append("\n---\n");
        }
        var prompt = $$"""
            You are a clinical information ranker. Given the user query and {{candidates.Count}} candidate passages,
            score each passage's relevance from 0 (irrelevant) to 10 (perfect).
            Output STRICT JSON only, no prose: {"scores":[{"i":0,"s":0.0}, ...]}.

            Query: {{query}}

            Candidates:
            {{sb}}
            """;

        try
        {
            var chat = llm.SelectChat();
            var resp = await chat.CompleteAsync(new ChatRequest(
                [new ChatMessage("system", "You output only valid JSON."), new ChatMessage("user", prompt)],
                Temperature: 0f), ct);
            var json = ExtractJson(resp.Content);
            using var doc = JsonDocument.Parse(json);
            var scored = new List<(int idx, double score)>();
            foreach (var el in doc.RootElement.GetProperty("scores").EnumerateArray())
            {
                scored.Add((el.GetProperty("i").GetInt32(), el.GetProperty("s").GetDouble()));
            }
            return scored
                .OrderByDescending(x => x.score)
                .Take(finalK)
                .Select(x => candidates[x.idx] with { Score = (float)x.score / 10f })
                .ToList();
        }
        catch
        {
            return candidates.Take(finalK).ToList();
        }
    }

    private static string ExtractJson(string text)
    {
        var start = text.IndexOf('{', StringComparison.Ordinal);
        var end = text.LastIndexOf('}');
        return start >= 0 && end > start ? text[start..(end + 1)] : text;
    }

    private static string Format(double d) => d.ToString("0.0", CultureInfo.InvariantCulture);
}
