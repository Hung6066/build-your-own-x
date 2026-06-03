using Hope.Agent.Infrastructure.Memory;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Qdrant.Client;
using Qdrant.Client.Grpc;

namespace Hope.Agent.Infrastructure.Maintenance;

public sealed class MemoryMaintenanceOptions
{
    public const string Section = "MemoryMaintenance";
    public bool Enabled { get; set; }
    public int IntervalHours { get; set; } = 24;
    public int RunHourUtc { get; set; } = 4;
    /// <summary>Only memories older than this are eligible for forgetting.</summary>
    public int MaxAgeDays { get; set; } = 365;
    /// <summary>Old memories whose importance is below this are forgotten (archived/deleted).</summary>
    public float ForgetImportanceBelow { get; set; } = 0.3f;
    public int MaxDeletesPerRun { get; set; } = 500;
    public uint ScrollBatchSize { get; set; } = 256;
}

/// <summary>
/// Periodic forgetting/archival pass. Scrolls the memory collection and removes memories that are both
/// old (older than <see cref="MemoryMaintenanceOptions.MaxAgeDays"/>) and low-importance, keeping the
/// store dense and retrieval fast. Importance is reinforced elsewhere (consolidation NOOP / dedup), so
/// frequently-recalled memories survive. Fully fail-open.
/// </summary>
internal sealed class MemoryMaintenanceHostedService(
    QdrantClient client,
    QdrantOptions qdrant,
    IOptions<MemoryMaintenanceOptions> opts,
    ILogger<MemoryMaintenanceHostedService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = opts.Value;
        if (!o.Enabled)
        {
            log.LogInformation("Memory maintenance (forgetting) disabled.");
            return;
        }
        log.LogInformation("Memory maintenance started (every {Hours}h at {Hour:00}:00 UTC, forget age>{Age}d & importance<{Imp}).",
            o.IntervalHours, o.RunHourUtc, o.MaxAgeDays, o.ForgetImportanceBelow);

        DateTimeOffset? lastRun = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromHours(1), stoppingToken); }
            catch (OperationCanceledException) { return; }

            var now = DateTimeOffset.UtcNow;
            if (now.Hour != o.RunHourUtc) continue;
            if (lastRun is { } prev && (now - prev).TotalHours < o.IntervalHours) continue;
            lastRun = now;

            try { await RunOnceAsync(o, stoppingToken); }
            catch (Exception ex) { log.LogError(ex, "Memory maintenance pass failed"); }
        }
    }

    private async Task RunOnceAsync(MemoryMaintenanceOptions o, CancellationToken ct)
    {
        if (!await client.CollectionExistsAsync(qdrant.Collection, ct))
            return;

        var cutoffMs = DateTimeOffset.UtcNow.AddDays(-o.MaxAgeDays).ToUnixTimeMilliseconds();
        var toDelete = new List<Guid>();
        PointId? offset = null;
        var scanned = 0;

        while (!ct.IsCancellationRequested && toDelete.Count < o.MaxDeletesPerRun)
        {
            var page = await client.ScrollAsync(
                qdrant.Collection,
                limit: o.ScrollBatchSize,
                offset: offset,
                payloadSelector: new WithPayloadSelector { Enable = true },
                cancellationToken: ct);

            if (page.Result.Count == 0) break;

            foreach (var p in page.Result)
            {
                scanned++;
                if (!p.Payload.TryGetValue("created_at", out var createdVal)) continue;
                var createdMs = createdVal.IntegerValue;
                var importance = p.Payload.TryGetValue("importance", out var impVal) ? (float)impVal.DoubleValue : 0.5f;

                if (createdMs < cutoffMs && importance < o.ForgetImportanceBelow
                    && p.Id is { } id && Guid.TryParse(id.Uuid, out var gid))
                {
                    toDelete.Add(gid);
                    if (toDelete.Count >= o.MaxDeletesPerRun) break;
                }
            }

            if (page.NextPageOffset is null) break;
            offset = page.NextPageOffset;
        }

        if (toDelete.Count > 0)
        {
            await client.DeleteAsync(qdrant.Collection, toDelete, cancellationToken: ct);
            log.LogInformation("Memory maintenance forgot {Deleted} memories (scanned {Scanned}).", toDelete.Count, scanned);
        }
        else
        {
            log.LogInformation("Memory maintenance scanned {Scanned}; nothing to forget.", scanned);
        }
    }
}
