using System.Text.Json;
using Hope.Agent.Application.Migration;
using Hope.Agent.Domain.Learning;
using Hope.Agent.Infrastructure.Persistence;
using Hope.Agent.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hope.Agent.Infrastructure.Migration;

/// <summary>
/// Imports external chatbot FAQ datasets as LearnedSkill rows.
/// Supports Dialogflow CX/ES FAQ export, RASA NLU YAML/JSON, and a generic
/// { question, answer, intent? }[] payload.
/// </summary>
internal sealed class ExternalChatbotImporter(
    AgentDbContext db,
    IClock clock,
    IOptions<MigrationOptions> opts,
    ILogger<ExternalChatbotImporter> log) : IExternalImporter
{
    public async Task<ImportStats> ImportAsync(ImportRequest request, CancellationToken ct)
    {
        var o = opts.Value;
        if (!o.Enabled)
            return new ImportStats(0, 0, 0, new[] { "Migration disabled in configuration." });

        var items = request.Source switch
        {
            ExternalSource.DialogflowFaq => await ReadDialogflowAsync(request.Payload, ct),
            ExternalSource.Rasa => await ReadRasaAsync(request.Payload, ct),
            ExternalSource.GenericFaq => await ReadGenericAsync(request.Payload, ct),
            _ => Array.Empty<FaqItem>(),
        };

        if (items.Length > o.MaxItemsPerImport)
            items = items[..o.MaxItemsPerImport];

        var warnings = new List<string>();
        var now = clock.UtcNow;
        var imported = 0;
        var skipped = 0;
        var defaultIntent = request.Intent ?? "imported";

        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(item.Question) || string.IsNullOrWhiteSpace(item.Answer))
            {
                skipped++;
                continue;
            }
            var sig = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(item.Question.Trim().ToLowerInvariant())))[..32];
            var exists = await db.LearnedSkills.AnyAsync(x => x.Signature == sig, ct);
            if (exists) { skipped++; continue; }

            if (request.DryRun) { imported++; continue; }

            db.LearnedSkills.Add(new LearnedSkill
            {
                Id = Guid.CreateVersion7(),
                Intent = item.Intent ?? defaultIntent,
                Signature = sig,
                ToolSequenceJson = "[]",
                AnswerTemplate = item.Answer.Trim(),
                Reward = 0.5,
                UsageCount = 0,
                CreatedAt = now,
                LastUsed = now,
            });
            imported++;
        }

        if (!request.DryRun)
            await db.SaveChangesAsync(ct);

        log.LogInformation("Imported {Imported}/{Total} items from {Source} (dry={Dry}).",
            imported, items.Length, request.Source, request.DryRun);
        return new ImportStats(items.Length, imported, skipped, warnings);
    }

    private static async Task<FaqItem[]> ReadDialogflowAsync(Stream s, CancellationToken ct)
    {
        var doc = await JsonDocument.ParseAsync(s, cancellationToken: ct);
        var items = new List<FaqItem>();
        // Two common shapes: { intents: [{ displayName, trainingPhrases:[{parts:[{text}]}], messages:[{text:{text:[..]}}] }] }
        // or a flat array exported from FAQ Knowledge bases: [{ question, answer }]
        if (doc.RootElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in doc.RootElement.EnumerateArray())
                items.Add(ExtractGeneric(el));
        }
        else if (doc.RootElement.TryGetProperty("intents", out var intents))
        {
            foreach (var intent in intents.EnumerateArray())
            {
                var name = intent.TryGetProperty("displayName", out var dn) ? dn.GetString() : null;
                var question = intent.TryGetProperty("trainingPhrases", out var tp)
                    ? tp.EnumerateArray().FirstOrDefault().TryGetProperty("parts", out var parts)
                        ? string.Concat(parts.EnumerateArray().Select(p => p.TryGetProperty("text", out var t) ? t.GetString() : null))
                        : null
                    : null;
                var answer = intent.TryGetProperty("messages", out var msgs)
                    ? msgs.EnumerateArray().FirstOrDefault().TryGetProperty("text", out var txt)
                        ? txt.TryGetProperty("text", out var arr)
                            ? string.Join(" ", arr.EnumerateArray().Select(a => a.GetString()))
                            : null
                        : null
                    : null;
                if (question is not null && answer is not null)
                    items.Add(new FaqItem(question, answer, name));
            }
        }
        return items.ToArray();
    }

    private static async Task<FaqItem[]> ReadRasaAsync(Stream s, CancellationToken ct)
    {
        // RASA NLU exported as JSON: { rasa_nlu_data: { common_examples: [{ text, intent }] } }
        // Answers come from a parallel responses.json — we treat the intent name as both pivot and template hint.
        var doc = await JsonDocument.ParseAsync(s, cancellationToken: ct);
        var items = new List<FaqItem>();
        if (doc.RootElement.TryGetProperty("rasa_nlu_data", out var data) &&
            data.TryGetProperty("common_examples", out var ex))
        {
            foreach (var e in ex.EnumerateArray())
            {
                var text = e.TryGetProperty("text", out var t) ? t.GetString() : null;
                var intent = e.TryGetProperty("intent", out var i) ? i.GetString() : null;
                if (text is not null && intent is not null)
                    items.Add(new FaqItem(text, $"[Intent recognised: {intent}] (template — fill in clinical answer)", intent));
            }
        }
        return items.ToArray();
    }

    private static async Task<FaqItem[]> ReadGenericAsync(Stream s, CancellationToken ct)
    {
        var doc = await JsonDocument.ParseAsync(s, cancellationToken: ct);
        var items = new List<FaqItem>();
        if (doc.RootElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in doc.RootElement.EnumerateArray())
                items.Add(ExtractGeneric(el));
        }
        return items.ToArray();
    }

    private static FaqItem ExtractGeneric(JsonElement el) => new(
        Question: el.TryGetProperty("question", out var q) ? q.GetString() ?? string.Empty : string.Empty,
        Answer: el.TryGetProperty("answer", out var a) ? a.GetString() ?? string.Empty : string.Empty,
        Intent: el.TryGetProperty("intent", out var i) ? i.GetString() : null);

    private sealed record FaqItem(string Question, string Answer, string? Intent);
}
