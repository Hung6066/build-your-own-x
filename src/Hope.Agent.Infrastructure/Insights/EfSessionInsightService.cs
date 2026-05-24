using Hope.Agent.Application.Insights;
using Hope.Agent.Application.LLM;
using Hope.Agent.Application.Observability;
using Hope.Agent.Domain.Conversations;
using Hope.Agent.Domain.Insights;
using Hope.Agent.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Hope.Agent.Infrastructure.Insights;

internal sealed class EfSessionInsightService(
    AgentDbContext db,
    ILLMRouter llm,
    ILogger<EfSessionInsightService> log) : ISessionInsightService
{
    public async Task<IReadOnlyList<SessionSummary>> RecentAsync(Guid userId, int days, CancellationToken ct)
    {
        var since = DateTimeOffset.UtcNow.AddDays(-Math.Max(1, days));
        return await db.SessionSummaries.AsNoTracking()
            .Where(s => s.UserId == userId && s.PeriodEnd >= since)
            .OrderByDescending(s => s.PeriodEnd)
            .Take(50)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<SessionSummary>> SearchAsync(Guid userId, string query, int take, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Array.Empty<SessionSummary>();

        // Postgres full-text search via to_tsvector at query time; the 'simple' config keeps
        // Vietnamese tokens unstemmed which is appropriate for clinical jargon.
        return await db.SessionSummaries.AsNoTracking()
            .Where(s => s.UserId == userId
                && EF.Functions.ToTsVector("simple", s.Content)
                    .Matches(EF.Functions.WebSearchToTsQuery("simple", query)))
            .OrderByDescending(s => s.CreatedAt)
            .Take(Math.Clamp(take, 1, 100))
            .ToListAsync(ct);
    }

    public async Task<SessionSummary?> GenerateAsync(Guid userId, DateTimeOffset periodStart, DateTimeOffset periodEnd, CancellationToken ct)
    {
        var convs = await db.Conversations.AsNoTracking()
            .Where(c => c.UserId == userId && c.UpdatedAt >= periodStart && c.UpdatedAt < periodEnd)
            .OrderByDescending(c => c.UpdatedAt)
            .Take(50)
            .ToListAsync(ct);
        if (convs.Count == 0) return null;

        var convIds = convs.Select(c => c.Id).ToList();
        var messages = await db.Messages.AsNoTracking()
            .Where(m => convIds.Contains(m.ConversationId)
                && (m.Role == MessageRole.User || m.Role == MessageRole.Assistant))
            .OrderBy(m => m.CreatedAt)
            .Take(500)
            .ToListAsync(ct);
        if (messages.Count == 0) return null;

        var transcript = string.Join("\n", messages.Select(m =>
            $"{(m.Role == MessageRole.User ? "U" : "A")}: {Truncate(m.Content, 300)}"));

        var sys = "You write a weekly clinical assistant usage summary for one clinician. " +
                  "Cover: dominant topics, open follow-ups, recurring tool calls, notable risks. " +
                  "Be terse. 6-10 bullet points. Use Vietnamese if the dialogue is mostly Vietnamese.";

        string content;
        try
        {
            var chat = llm.SelectChat();
            var resp = await chat.CompleteAsync(new ChatRequest(
                [new ChatMessage("system", sys), new ChatMessage("user", transcript)],
                Temperature: 0.2f,
                MaxTokens: 700), ct);
            content = resp.Content.Trim();
            if (string.IsNullOrWhiteSpace(content)) return null;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Session summary LLM call failed for user {UserId}", userId);
            return null;
        }

        var summary = new SessionSummary
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            PeriodStart = periodStart,
            PeriodEnd = periodEnd,
            ConversationCount = convs.Count,
            Content = content,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await db.SessionSummaries.AddAsync(summary, ct);
        await db.SaveChangesAsync(ct);
        HopeMeters.SessionSummariesGenerated.Add(1);
        return summary;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
