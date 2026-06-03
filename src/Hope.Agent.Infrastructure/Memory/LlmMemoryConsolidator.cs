using System.Text;
using System.Text.Json;
using Hope.Agent.Application.Abstractions;
using Hope.Agent.Application.Knowledge;
using Hope.Agent.Application.LLM;
using Hope.Agent.Application.Memory;
using Hope.Agent.Domain.Memory;
using Microsoft.Extensions.Logging;

namespace Hope.Agent.Infrastructure.Memory;

/// <summary>
/// Mem0/A-Mem-style memory consolidator. For each finished turn it asks an LLM to extract durable
/// atomic facts and reconcile them against the user's existing related memories, emitting
/// ADD / UPDATE / DELETE / NOOP operations. New and updated facts are also pushed into the knowledge
/// graph and linked back to their memory record, giving graph-aware recall. Fully fail-open.
/// </summary>
internal sealed class LlmMemoryConsolidator(
    ILLMRouter router,
    IMemoryStore memory,
    IKnowledgeExtractor knowledgeExtractor,
    IKnowledgeGraphStore knowledgeGraph,
    ILogger<LlmMemoryConsolidator> log) : IMemoryConsolidator
{
    private const string SystemPrompt = """
        You maintain the long-term memory of a clinical operations AI. You are given the latest
        conversation turn and the user's EXISTING related memories (each with an id). Extract only
        DURABLE facts worth remembering across sessions (preferences, stable clinical facts, decisions,
        identifiers). Ignore small talk and transient context. Reconcile each fact with existing
        memories and output STRICT JSON:
        {"operations":[{"op":"ADD|UPDATE|DELETE|NOOP","id":"<existing memory id or empty>","content":"concise self-contained fact","kind":"episodic|semantic|procedural|clinical","importance":0.0-1.0}]}
        Rules:
        - ADD: a genuinely new fact (id empty).
        - UPDATE: the new fact supersedes an existing one (set id to that memory's id; content = the new value).
        - DELETE: an existing fact is now wrong/retracted (set id; content may be empty).
        - NOOP: already known and unchanged (set id).
        - Each content must be a single self-contained fact, no pronouns referring outside it.
        - Never fabricate. If nothing is worth storing, return {"operations":[]}.
        Return JSON only, no prose.
        """;

    public async Task ConsolidateAsync(MemoryConsolidationContext context, CancellationToken ct)
    {
        try
        {
            var turn = $"User: {context.UserMessage}\nAssistant: {context.AssistantReply}";

            var embedder = router.SelectEmbedding();
            var turnVec = (await embedder.EmbedAsync(new EmbeddingRequest([turn]), ct)).Vectors[0];
            var existing = await memory.SearchAsync(context.UserId, turnVec, topK: 8, kind: null, ct);

            var prompt = BuildUserPrompt(turn, existing);
            var chat = router.SelectChat();
            var resp = await chat.CompleteAsync(new ChatRequest(
                Messages: [new("system", SystemPrompt), new("user", prompt)],
                Temperature: 0.0f,
                MaxTokens: 900), ct);

            var ops = ParseOperations(resp.Content);
            foreach (var op in ops)
                await ApplyAsync(context, op, ct);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Memory consolidation failed for user={UserId}", context.UserId);
        }
    }

    private static string BuildUserPrompt(string turn, IReadOnlyList<MemorySearchHit> existing)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Latest turn");
        sb.AppendLine(turn);
        sb.AppendLine();
        sb.AppendLine("## Existing related memories");
        if (existing.Count == 0)
        {
            sb.AppendLine("(none)");
        }
        else
        {
            foreach (var h in existing)
                sb.AppendLine($"- id={h.Record.Id} ({h.Record.Kind}): {h.Record.Content}");
        }
        return sb.ToString();
    }

    private async Task ApplyAsync(MemoryConsolidationContext context, MemoryOperation op, CancellationToken ct)
    {
        switch (op.Op)
        {
            case MemoryOperationKind.Delete when op.TargetId is { } del:
                await memory.DeleteAsync(del, ct);
                break;

            case MemoryOperationKind.Noop when op.TargetId is { } noop:
                await memory.BumpImportanceAsync(noop, 0.05f, ct);
                break;

            case MemoryOperationKind.Update when op.TargetId is { } old:
                await memory.DeleteAsync(old, ct);
                await WriteAsync(context, op, ct);
                break;

            case MemoryOperationKind.Add:
                await WriteAsync(context, op, ct);
                break;
        }
    }

    private async Task WriteAsync(MemoryConsolidationContext context, MemoryOperation op, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(op.Content)) return;

        var embedder = router.SelectEmbedding();
        var vec = (await embedder.EmbedAsync(new EmbeddingRequest([op.Content]), ct)).Vectors[0];
        var metadata = string.IsNullOrWhiteSpace(context.AgentProfile)
            ? new Dictionary<string, string>()
            : new Dictionary<string, string> { ["agent_profile"] = context.AgentProfile! };

        var record = new MemoryRecord
        {
            Id = Guid.CreateVersion7(),
            UserId = context.UserId,
            ConversationId = context.ConversationId,
            Kind = op.Kind,
            Content = op.Content,
            Source = "consolidator",
            Importance = Math.Clamp(op.Importance, 0f, 1f),
            Metadata = metadata,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await memory.UpsertAsync(record, vec, ct);

        // Graph-memory link: extract entities from the fact and connect them to this memory record.
        await LinkToGraphAsync(record, ct);
    }

    private async Task LinkToGraphAsync(MemoryRecord record, CancellationToken ct)
    {
        try
        {
            var extracted = await knowledgeExtractor.ExtractAsync(record.Content, ct);
            if (extracted.Entities.Count == 0) return;
            await knowledgeGraph.UpsertAsync(extracted, ct);
            await knowledgeGraph.LinkMemoryAsync(
                record.Id, record.UserId,
                extracted.Entities.Select(e => e.Id).ToList(), ct);
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Graph-memory linking skipped for memory={MemoryId}", record.Id);
        }
    }

    private static IReadOnlyList<MemoryOperation> ParseOperations(string raw)
    {
        var ops = new List<MemoryOperation>();
        try
        {
            var json = ExtractJson(raw);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("operations", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return ops;

            foreach (var el in arr.EnumerateArray())
            {
                var opStr = el.TryGetProperty("op", out var o) ? o.GetString() : null;
                if (!TryParseOp(opStr, out var kind)) continue;

                var content = el.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";
                var memKind = ParseKind(el.TryGetProperty("kind", out var k) ? k.GetString() : null);
                var importance = el.TryGetProperty("importance", out var imp) && imp.ValueKind == JsonValueKind.Number
                    ? (float)imp.GetDouble() : 0.5f;
                Guid? targetId = el.TryGetProperty("id", out var idEl)
                    && Guid.TryParse(idEl.GetString(), out var gid) ? gid : null;

                // Drop nonsensical combinations rather than risk corrupting the store.
                if ((kind is MemoryOperationKind.Update or MemoryOperationKind.Delete or MemoryOperationKind.Noop) && targetId is null)
                    continue;
                if (kind is MemoryOperationKind.Add or MemoryOperationKind.Update && string.IsNullOrWhiteSpace(content))
                    continue;

                ops.Add(new MemoryOperation(kind, content, memKind, importance, targetId));
            }
        }
        catch
        {
            // fail-open: no operations
        }
        return ops;
    }

    private static bool TryParseOp(string? s, out MemoryOperationKind kind)
    {
        kind = s?.Trim().ToUpperInvariant() switch
        {
            "ADD" => MemoryOperationKind.Add,
            "UPDATE" => MemoryOperationKind.Update,
            "DELETE" => MemoryOperationKind.Delete,
            "NOOP" => MemoryOperationKind.Noop,
            _ => MemoryOperationKind.Noop,
        };
        return s is not null;
    }

    private static MemoryKind ParseKind(string? s) => s?.Trim().ToLowerInvariant() switch
    {
        "semantic" => MemoryKind.Semantic,
        "procedural" => MemoryKind.Procedural,
        "clinical" => MemoryKind.Clinical,
        _ => MemoryKind.Episodic,
    };

    private static string ExtractJson(string s)
    {
        var start = s.IndexOf('{');
        var end = s.LastIndexOf('}');
        return start >= 0 && end > start ? s.Substring(start, end - start + 1) : s;
    }
}
