using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hope.Agent.Application.Abstractions;
using Hope.Agent.Application.Security;
using Hope.Agent.Domain.Audit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hope.Agent.Infrastructure.Persistence;

internal sealed class AuditImmutabilityWorker(
    IServiceScopeFactory scopeFactory,
    IOptionsMonitor<AuditImmutabilityOptions> options,
    ILogger<AuditImmutabilityWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var opts = options.CurrentValue;
            try
            {
                if (opts.Enabled)
                    await VerifyOnceAsync(opts, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex)
            {
                log.LogError(ex, "Audit immutability verification pass failed");
            }

            await Task.Delay(TimeSpan.FromMinutes(Math.Max(opts.VerifyIntervalMinutes, 5)), stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task VerifyOnceAsync(AuditImmutabilityOptions opts, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentDbContext>();
        var audit = scope.ServiceProvider.GetRequiredService<IAuditSink>();
        var since = DateTimeOffset.UtcNow.AddDays(-Math.Max(opts.VerificationLookbackDays, 1));
        var events = await db.AuditEvents.AsNoTracking()
            .Where(x => x.OccurredAt >= since)
            .OrderBy(x => x.OccurredAt)
            .ThenBy(x => x.Id)
            .Take(50_000)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var failures = 0;
        foreach (var evt in events)
        {
            if (!TryVerify(evt, out var reason))
            {
                failures++;
                log.LogCritical("Audit hash-chain verification failed for {AuditId}: {Reason}", evt.Id, reason);
            }
        }

        await audit.WriteAsync(new AuditEvent
        {
            Id = Guid.CreateVersion7(),
            TenantId = null,
            OccurredAt = DateTimeOffset.UtcNow,
            Actor = "system:audit-immutability-worker",
            Action = failures == 0 ? "audit.chain.verified" : "audit.chain.verification_failed",
            ResourceType = "audit_logs",
            ResourceId = $"lookback_days:{opts.VerificationLookbackDays}",
            Reason = failures == 0 ? "hash_chain_verified" : "hash_chain_mismatch",
            PayloadJson = JsonSerializer.Serialize(new
            {
                checkedEvents = events.Count,
                failures,
                opts.WormArchiveUri,
                opts.RequireWormArchive,
            }),
        }, ct).ConfigureAwait(false);
    }

    private static bool TryVerify(AuditEvent evt, out string reason)
    {
        reason = string.Empty;
        try
        {
            using var doc = JsonDocument.Parse(evt.PayloadJson);
            var root = doc.RootElement;
            if (!root.TryGetProperty("chain", out var chain) || !root.TryGetProperty("data", out var data))
            {
                reason = "missing_chain_envelope";
                return false;
            }

            var prev = chain.GetProperty("prev").GetString();
            var expected = chain.GetProperty("hash").GetString();
            var actual = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{prev}|{evt.Id:N}|{data.GetRawText()}"))).ToLowerInvariant();
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                reason = "hash_mismatch";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            reason = ex.Message;
            return false;
        }
    }
}
