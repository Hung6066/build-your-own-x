using System.Text.Json;
using Hope.Agent.Application.LLM;
using Hope.Agent.Application.Observability;
using Hope.Agent.Application.UserModeling;
using Hope.Agent.Domain.Conversations;
using Hope.Agent.Domain.UserModeling;
using Hope.Agent.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Neo4j.Driver;

namespace Hope.Agent.Infrastructure.UserModeling;

/// <summary>
/// Honcho-style user-model service: every N user turns, asks an LLM to extract a short
/// structured profile (role/specialty/communication style/language) from recent messages.
/// Persists to Postgres (read-cache) and Neo4j as a <c>:Clinician</c> node (authoritative).
/// </summary>
internal sealed class LlmUserModelService(
    AgentDbContext db,
    ILLMRouter llm,
    IDriver neo4j,
    IOptions<UserModelOptions> opts,
    ILogger<LlmUserModelService> log) : IUserModelService
{
    private readonly UserModelOptions _opts = opts.Value;

    public async Task<UserTraitsSnapshot?> GetAsync(Guid userId, CancellationToken ct)
    {
        var row = await db.UserTraits.AsNoTracking().FirstOrDefaultAsync(t => t.UserId == userId, ct);
        return row is null
            ? null
            : new UserTraitsSnapshot(row.Role, row.Specialty, row.CommunicationStyle, row.PreferredLanguage);
    }

    public async Task TryExtractAsync(Guid userId, Guid conversationId, CancellationToken ct)
    {
        if (!_opts.Enabled) return;

        var totalTurns = await db.Messages.AsNoTracking()
            .Where(m => m.ConversationId == conversationId && m.Role == MessageRole.User)
            .CountAsync(ct);

        var existing = await db.UserTraits.FirstOrDefaultAsync(t => t.UserId == userId, ct);
        var delta = totalTurns - (existing?.TurnsAtLastExtract ?? 0);
        if (delta < _opts.ExtractEveryTurns) return;

        var window = Math.Max(_opts.RecentTurnsWindow, _opts.ExtractEveryTurns);
        var recent = await db.Messages.AsNoTracking()
            .Where(m => m.ConversationId == conversationId
                && (m.Role == MessageRole.User || m.Role == MessageRole.Assistant))
            .OrderByDescending(m => m.CreatedAt)
            .Take(window)
            .ToListAsync(ct);
        recent.Reverse();
        if (recent.Count == 0) return;

        var snapshot = await ExtractWithLlmAsync(recent, ct);
        if (snapshot is null) return;

        var now = DateTimeOffset.UtcNow;
        if (existing is null)
        {
            await db.UserTraits.AddAsync(new UserTrait
            {
                UserId = userId,
                Role = snapshot.Role,
                Specialty = snapshot.Specialty,
                CommunicationStyle = snapshot.CommunicationStyle,
                PreferredLanguage = snapshot.PreferredLanguage,
                TurnsAtLastExtract = totalTurns,
                UpdatedAt = now,
            }, ct);
        }
        else
        {
            existing.Role = snapshot.Role ?? existing.Role;
            existing.Specialty = snapshot.Specialty ?? existing.Specialty;
            existing.CommunicationStyle = snapshot.CommunicationStyle ?? existing.CommunicationStyle;
            existing.PreferredLanguage = snapshot.PreferredLanguage ?? existing.PreferredLanguage;
            existing.TurnsAtLastExtract = totalTurns;
            existing.UpdatedAt = now;
        }
        await db.SaveChangesAsync(ct);

        await UpsertClinicianNodeAsync(userId, snapshot, ct);
        HopeMeters.UserModelExtractions.Add(1);
    }

    private async Task<UserTraitsSnapshot?> ExtractWithLlmAsync(List<ConversationMessage> recent, CancellationToken ct)
    {
        var transcript = string.Join("\n", recent.Select(m =>
            $"{(m.Role == MessageRole.User ? "User" : "Assistant")}: {Truncate(m.Content, 400)}"));

        var sys = "You extract a structured clinician profile from a healthcare-staff dialogue. " +
                  "Return ONLY a compact JSON object with optional fields: role, specialty, communication_style, language. " +
                  "Use null when unknown. Examples — role: 'nurse'|'doctor'|'admin'; specialty: 'cardiology'|'pediatrics'|...; " +
                  "communication_style: 'concise'|'detailed'|'formal'|'friendly'; language: 'vi'|'en'.";

        var req = new ChatRequest(
            [new ChatMessage("system", sys), new ChatMessage("user", transcript)],
            Temperature: 0.1f,
            MaxTokens: 200);

        try
        {
            var chat = llm.SelectChat();
            var resp = await chat.CompleteAsync(req, ct);
            var json = ExtractJson(resp.Content);
            if (string.IsNullOrWhiteSpace(json)) return null;
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            return new UserTraitsSnapshot(
                Role: ReadString(root, "role"),
                Specialty: ReadString(root, "specialty"),
                CommunicationStyle: ReadString(root, "communication_style"),
                PreferredLanguage: ReadString(root, "language"));
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "User model extraction LLM call failed");
            return null;
        }
    }

    private async Task UpsertClinicianNodeAsync(Guid userId, UserTraitsSnapshot s, CancellationToken ct)
    {
        try
        {
            await using var session = neo4j.AsyncSession();
            await session.ExecuteWriteAsync(async tx =>
            {
                await tx.RunAsync(@"
                    MERGE (c:Clinician {id: $id})
                    SET c.role = coalesce($role, c.role),
                        c.specialty = coalesce($specialty, c.specialty),
                        c.communicationStyle = coalesce($style, c.communicationStyle),
                        c.language = coalesce($lang, c.language),
                        c.updatedAt = $updatedAt",
                    new
                    {
                        id = userId.ToString(),
                        role = (object?)s.Role ?? null!,
                        specialty = (object?)s.Specialty ?? null!,
                        style = (object?)s.CommunicationStyle ?? null!,
                        lang = (object?)s.PreferredLanguage ?? null!,
                        updatedAt = DateTime.UtcNow,
                    });
            });
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Failed to upsert :Clinician node for user {UserId}", userId);
        }
    }

    private static string? ReadString(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private static string ExtractJson(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        return start >= 0 && end > start ? text[start..(end + 1)] : string.Empty;
    }
}
