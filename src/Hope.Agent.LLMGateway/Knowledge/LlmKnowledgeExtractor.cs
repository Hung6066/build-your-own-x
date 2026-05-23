using System.Text.Json;
using Hope.Agent.Application.Knowledge;
using Hope.Agent.Application.LLM;
using Hope.Agent.Domain.Knowledge;

namespace Hope.Agent.LLMGateway.Knowledge;

/// <summary>LLM-driven entity/relation extractor (Microsoft GraphRAG style).</summary>
internal sealed class LlmKnowledgeExtractor(ILLMRouter router) : IKnowledgeExtractor
{
    private const string SystemPrompt = """
        You are a clinical knowledge-graph extractor. Read the text and emit STRICT JSON:
        {"entities":[{"id":"slug","name":"...","type":"Person|Drug|Condition|Procedure|Facility|Concept","description":"short"}],
         "relations":[{"source":"slug","target":"slug","predicate":"TREATS|INDICATED_FOR|CONTRAINDICATES|CAUSES|WORKS_AT|MENTIONS","confidence":0..1,"evidence":"short quote"}]}
        Rules: slug = lower kebab-case of name; never invent facts; skip ambiguous;
        ids must be referenced consistently between entities and relations; max 20 entities + 30 relations.
        Return JSON only, no prose.
        """;

    public async Task<ExtractedKnowledge> ExtractAsync(string text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text)) return new ExtractedKnowledge();

        var chat = router.SelectChat();
        var resp = await chat.CompleteAsync(new ChatRequest(
            Messages: new ChatMessage[]
            {
                new("system", SystemPrompt),
                new("user", text),
            },
            Temperature: 0.0f,
            MaxTokens: 1200), ct);

        return Parse(resp.Content);
    }

    private static ExtractedKnowledge Parse(string raw)
    {
        try
        {
            var json = ExtractJson(raw);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var now = DateTimeOffset.UtcNow;

            var entities = new List<KgEntity>();
            if (root.TryGetProperty("entities", out var ents) && ents.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in ents.EnumerateArray())
                {
                    var id = e.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                    var name = e.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
                    if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name)) continue;
                    entities.Add(new KgEntity
                    {
                        Id = id!,
                        Name = name!,
                        Type = e.TryGetProperty("type", out var t) ? (t.GetString() ?? "Concept") : "Concept",
                        Description = e.TryGetProperty("description", out var d) ? d.GetString() : null,
                        FirstSeen = now,
                        LastSeen = now,
                        Mentions = 1,
                    });
                }
            }

            var relations = new List<KgRelation>();
            if (root.TryGetProperty("relations", out var rels) && rels.ValueKind == JsonValueKind.Array)
            {
                foreach (var r in rels.EnumerateArray())
                {
                    var src = r.TryGetProperty("source", out var s) ? s.GetString() : null;
                    var tgt = r.TryGetProperty("target", out var tg) ? tg.GetString() : null;
                    var pred = r.TryGetProperty("predicate", out var p) ? p.GetString() : null;
                    if (string.IsNullOrWhiteSpace(src) || string.IsNullOrWhiteSpace(tgt) || string.IsNullOrWhiteSpace(pred)) continue;
                    relations.Add(new KgRelation
                    {
                        SourceId = src!,
                        TargetId = tgt!,
                        Predicate = pred!,
                        Confidence = r.TryGetProperty("confidence", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetDouble() : 0.5,
                        Evidence = r.TryGetProperty("evidence", out var ev) ? ev.GetString() : null,
                        ObservedAt = now,
                    });
                }
            }

            return new ExtractedKnowledge { Entities = entities, Relations = relations };
        }
        catch
        {
            return new ExtractedKnowledge();
        }
    }

    private static string ExtractJson(string s)
    {
        var start = s.IndexOf('{');
        var end = s.LastIndexOf('}');
        return start >= 0 && end > start ? s.Substring(start, end - start + 1) : s;
    }
}
