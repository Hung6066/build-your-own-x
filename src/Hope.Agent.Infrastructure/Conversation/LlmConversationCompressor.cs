using Hope.Agent.Application.Compression;
using Hope.Agent.Application.LLM;
using Hope.Agent.Application.Observability;
using Hope.Agent.Domain.Conversations;
using Hope.Agent.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hope.Agent.Infrastructure.Compression;

/// <summary>
/// LLM-backed conversation compressor: when a conversation grows past the configured
/// threshold, summarizes all but the most recent K messages into a single text blob
/// that the orchestrator can inject as a system message in place of the older turns.
/// </summary>
internal sealed class LlmConversationCompressor(
    AgentDbContext db,
    ILLMRouter llm,
    IOptions<ConversationCompressorOptions> opts,
    ILogger<LlmConversationCompressor> log) : IConversationCompressor
{
    private readonly ConversationCompressorOptions _opts = opts.Value;

    public async Task<ConversationSummary?> GetSummaryAsync(Guid conversationId, CancellationToken ct) =>
        await db.ConversationSummaries.AsNoTracking()
            .FirstOrDefaultAsync(s => s.ConversationId == conversationId, ct);

    public async Task<CompressionResult?> MaybeCompressAsync(Domain.Conversations.Conversation conversation, CancellationToken ct)
    {
        if (!_opts.Enabled) return null;
        if (conversation.Messages.Count <= _opts.TriggerMessageCount) return null;

        var ordered = conversation.Messages.OrderBy(m => m.CreatedAt).ToList();
        var keep = Math.Max(1, _opts.KeepRecentMessages);
        if (ordered.Count <= keep) return null;

        var toCompress = ordered.Take(ordered.Count - keep).ToList();
        if (toCompress.Count == 0) return null;

        var existing = await db.ConversationSummaries.FirstOrDefaultAsync(
            s => s.ConversationId == conversation.Id, ct);
        var alreadyCovered = existing?.SummarizedMessageCount ?? 0;
        if (toCompress.Count <= alreadyCovered) return null;

        var transcript = string.Join("\n", toCompress.Select(m =>
            $"{Role(m.Role)}: {Truncate(m.Content, 600)}"));

        var sys = "You compress a clinical assistant conversation into a short structured summary. " +
                  "Preserve: patient identifiers (masked), open clinical questions, decisions made, " +
                  "tool calls performed and their outcomes, follow-ups owed. Be terse. Output 4-10 bullets.";

        string summaryText;
        try
        {
            var chat = llm.SelectChat();
            var resp = await chat.CompleteAsync(new ChatRequest(
                [new ChatMessage("system", sys), new ChatMessage("user", transcript)],
                Temperature: 0.1f,
                MaxTokens: 600), ct);
            summaryText = resp.Content.Trim();
            if (string.IsNullOrWhiteSpace(summaryText)) return null;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Conversation compression LLM call failed; skipping");
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        var upTo = toCompress[^1].CreatedAt;
        ConversationSummary summary;
        if (existing is null)
        {
            summary = new ConversationSummary
            {
                ConversationId = conversation.Id,
                Content = summaryText,
                SummarizedMessageCount = toCompress.Count,
                SummarizedUpTo = upTo,
                UpdatedAt = now,
            };
            await db.ConversationSummaries.AddAsync(summary, ct);
        }
        else
        {
            existing.Content = summaryText;
            existing.SummarizedMessageCount = toCompress.Count;
            existing.SummarizedUpTo = upTo;
            existing.UpdatedAt = now;
            summary = existing;
        }
        await db.SaveChangesAsync(ct);
        HopeMeters.ConversationsCompressed.Add(1);
        return new CompressionResult(summary, toCompress.Count);
    }

    private static string Role(MessageRole r) => r switch
    {
        MessageRole.User => "User",
        MessageRole.Assistant => "Assistant",
        MessageRole.Tool => "Tool",
        _ => "System",
    };

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
