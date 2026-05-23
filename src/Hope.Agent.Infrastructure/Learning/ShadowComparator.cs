using Hope.Agent.Application.Learning;
using Hope.Agent.Application.Observability;
using Hope.Agent.Domain.Learning;
using Hope.Agent.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Hope.Agent.Infrastructure.Learning;

internal sealed class ShadowComparator(AgentDbContext db, ILogger<ShadowComparator> log) : IShadowComparator
{
    public async Task<ChallengerConfig?> GetActiveChallengerAsync(string intent, CancellationToken ct)
    {
        return await db.ChallengerConfigs.AsNoTracking()
            .Where(c => c.Intent == intent && c.Active && !c.Promoted)
            .OrderByDescending(c => c.CreatedAt)
            .FirstOrDefaultAsync(ct);
    }

    public async Task RecordAsync(ShadowComparison comparison, CancellationToken ct)
    {
        db.ShadowComparisons.Add(comparison);
        await db.SaveChangesAsync(ct);

        HopeMeters.ShadowComparisons.Add(1,
            new KeyValuePair<string, object?>("intent", comparison.Intent),
            new KeyValuePair<string, object?>("won", comparison.ChallengerWon));

        var cfg = await db.ChallengerConfigs.FirstOrDefaultAsync(
            c => c.Intent == comparison.Intent && c.ChallengerProvider == comparison.ChallengerProvider && c.Active, ct);
        if (cfg is null || cfg.Promoted) return;

        var stats = await db.ShadowComparisons.AsNoTracking()
            .Where(s => s.Intent == cfg.Intent && s.ChallengerProvider == cfg.ChallengerProvider)
            .GroupBy(_ => 1)
            .Select(g => new { Total = g.Count(), Wins = g.Count(x => x.ChallengerWon) })
            .FirstOrDefaultAsync(ct);

        if (stats is null) return;
        if (stats.Total < cfg.MinSamples) return;

        var winRate = (double)stats.Wins / stats.Total;
        if (winRate >= cfg.PromotionWinRate)
        {
            cfg.Promoted = true;
            cfg.PromotedAt = DateTimeOffset.UtcNow;
            cfg.Active = false;
            await db.SaveChangesAsync(ct);
            HopeMeters.ChallengerPromotions.Add(1,
                new KeyValuePair<string, object?>("intent", cfg.Intent),
                new KeyValuePair<string, object?>("challenger", cfg.ChallengerProvider));
            log.LogInformation("Challenger {Challenger} promoted on intent {Intent} (winRate={Rate:F3}, n={Total})",
                cfg.ChallengerProvider, cfg.Intent, winRate, stats.Total);
        }
    }

    public async Task<IReadOnlyList<ShadowComparison>> RecentAsync(string intent, int take, CancellationToken ct)
    {
        return await db.ShadowComparisons.AsNoTracking()
            .Where(s => s.Intent == intent)
            .OrderByDescending(s => s.CreatedAt)
            .Take(take)
            .ToListAsync(ct);
    }

    public async Task<ChallengerConfig> UpsertChallengerAsync(ChallengerConfig config, CancellationToken ct)
    {
        var existing = await db.ChallengerConfigs.FirstOrDefaultAsync(
            c => c.Intent == config.Intent && c.ChallengerProvider == config.ChallengerProvider, ct);
        if (existing is null)
        {
            db.ChallengerConfigs.Add(config);
        }
        else
        {
            existing.TrafficFraction = config.TrafficFraction;
            existing.MinSamples = config.MinSamples;
            existing.PromotionWinRate = config.PromotionWinRate;
            existing.Active = config.Active;
            config = existing;
        }
        await db.SaveChangesAsync(ct);
        return config;
    }
}
