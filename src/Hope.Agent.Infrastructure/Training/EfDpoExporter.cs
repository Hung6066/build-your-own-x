using System.Text.Json;
using Hope.Agent.Application.Security;
using Hope.Agent.Application.Training;
using Hope.Agent.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Hope.Agent.Infrastructure.Training;

/// <summary>
/// Exports preference records as DPO JSONL compatible with HuggingFace TRL DPOTrainer.
/// Each line: { "prompt": string, "chosen": string, "rejected": string,
///              "specialty": string|null, "source": "hope-agent" }
/// The "prompt" field is the verbatim doctor turn; chosen/rejected are model responses.
/// </summary>
internal sealed class EfDpoExporter(
    AgentDbContext db,
    IPhiRedactor phi) : IDpoExporter
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    public async Task<DpoExportStats> ExportAsync(DpoExportFilter filter, Stream output, CancellationToken ct)
    {
        var q = db.PreferenceRecords.AsNoTracking();

        if (filter.Since is not null) q = q.Where(r => r.CreatedAt >= filter.Since.Value);
        if (filter.Until is not null) q = q.Where(r => r.CreatedAt <= filter.Until.Value);
        if (!string.IsNullOrWhiteSpace(filter.Specialty)) q = q.Where(r => r.Specialty == filter.Specialty);

        var records = await q
            .OrderByDescending(r => r.CreatedAt)
            .Take(filter.MaxRecords)
            .ToListAsync(ct);

        await using var writer = new StreamWriter(output, leaveOpen: true);
        long bytes = 0;

        foreach (var rec in records)
        {
            ct.ThrowIfCancellationRequested();

            var prompt = filter.RedactPhi ? phi.Redact(rec.Prompt) : rec.Prompt;
            var chosen = filter.RedactPhi ? phi.Redact(rec.ChosenResponse) : rec.ChosenResponse;
            var rejected = filter.RedactPhi ? phi.Redact(rec.RejectedResponse) : rec.RejectedResponse;

            var row = new
            {
                prompt,
                chosen,
                rejected,
                specialty = rec.Specialty,
                source = "hope-agent",
                created_at = rec.CreatedAt,
            };

            var line = JsonSerializer.Serialize(row, JsonOpts);
            await writer.WriteLineAsync(line.AsMemory(), ct);
            bytes += line.Length + 1;
        }

        await writer.FlushAsync(ct);
        return new DpoExportStats(records.Count, bytes);
    }
}
