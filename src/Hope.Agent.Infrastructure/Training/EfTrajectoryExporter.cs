using System.Text.Json;
using Hope.Agent.Application.Observability;
using Hope.Agent.Application.Security;
using Hope.Agent.Application.Training;
using Hope.Agent.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hope.Agent.Infrastructure.Training;

internal sealed class EfTrajectoryExporter(
    AgentDbContext db,
    IPhiRedactor phi,
    IOptions<TrajectoryExportOptions> opts,
    ILogger<EfTrajectoryExporter> log) : ITrajectoryExporter
{
    public async Task<TrajectoryExportStats> ExportAsync(TrajectoryExportFilter filter, Stream output, CancellationToken ct)
    {
        var max = filter.MaxConversations ?? opts.Value.DefaultMaxConversations;
        var since = filter.Since ?? DateTimeOffset.UtcNow.AddDays(-90);
        var until = filter.Until ?? DateTimeOffset.UtcNow;

        var convQuery = db.Conversations
            .AsNoTracking()
            .Where(c => c.UpdatedAt >= since && c.UpdatedAt <= until);
        if (filter.UserId is Guid u) convQuery = convQuery.Where(c => c.UserId == u);

        var convIds = await convQuery
            .OrderByDescending(c => c.UpdatedAt)
            .Take(max)
            .Select(c => c.Id)
            .ToListAsync(ct);

        var bytes = 0L;
        var msgTotal = 0;
        var convCount = 0;
        await using var writer = new StreamWriter(output, leaveOpen: true);

        foreach (var cid in convIds)
        {
            ct.ThrowIfCancellationRequested();
            var messages = await db.Messages
                .AsNoTracking()
                .Where(m => m.ConversationId == cid)
                .OrderBy(m => m.CreatedAt)
                .Select(m => new { m.Role, m.Content, m.ToolName, m.CreatedAt })
                .ToListAsync(ct);
            if (messages.Count < filter.MinTurns) continue;

            var record = new
            {
                conversation_id = cid,
                exported_at = DateTimeOffset.UtcNow,
                messages = messages.Select(m => new
                {
                    role = m.Role.ToString().ToLowerInvariant(),
                    tool = m.ToolName,
                    content = filter.RedactPhi ? phi.Redact(m.Content) : m.Content,
                    at = m.CreatedAt,
                }),
            };
            var line = JsonSerializer.Serialize(record);
            await writer.WriteLineAsync(line.AsMemory(), ct);
            bytes += line.Length + 1;
            msgTotal += messages.Count;
            convCount++;
        }

        await writer.FlushAsync(ct);
        HopeMeters.TrajectoriesExported.Add(convCount);
        log.LogInformation("TrajectoryExporter: wrote {Conv} conversations, {Msg} messages, {Bytes} bytes.", convCount, msgTotal, bytes);
        return new TrajectoryExportStats(convCount, msgTotal, bytes);
    }
}
