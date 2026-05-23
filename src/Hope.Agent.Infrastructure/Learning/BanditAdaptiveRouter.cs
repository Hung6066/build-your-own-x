using Hope.Agent.Application.LLM;
using Hope.Agent.Application.Learning;
using Hope.Agent.Domain.Learning;
using Hope.Agent.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Hope.Agent.Infrastructure.Learning;

/// <summary>
/// UCB1 bandit over chat providers, keyed by intent. Reward expected in [-1, +1].
/// Sparse stats yield exploration. Persists stats to RoutingStat table.
/// </summary>
internal sealed class BanditAdaptiveRouter(
    ILLMRouter fallback,
    IEnumerable<IChatCompletionProvider> providers,
    AgentDbContext db,
    ILogger<BanditAdaptiveRouter> log) : IAdaptiveRouter
{
    private const double ExplorationC = 1.4;
    private readonly IReadOnlyList<IChatCompletionProvider> _arms = providers.ToList();

    public async Task<RouterChoice> SelectChatAsync(string intent, CancellationToken ct)
    {
        if (_arms.Count <= 1)
        {
            var only = fallback.SelectChat();
            return new RouterChoice(only.Name, only.Name);
        }

        var stats = await db.RoutingStats.AsNoTracking()
            .Where(s => s.Intent == intent)
            .ToListAsync(ct);

        var totalPulls = Math.Max(1, stats.Sum(s => s.Pulls));
        IChatCompletionProvider best = _arms[0];
        double bestScore = double.NegativeInfinity;

        foreach (var arm in _arms)
        {
            var stat = stats.Find(s => s.Provider == arm.Name);
            double avgReward = stat is { Pulls: > 0 } ? stat.TotalReward / stat.Pulls : 0;
            double bonus = stat is null || stat.Pulls == 0
                ? double.PositiveInfinity
                : ExplorationC * Math.Sqrt(Math.Log(totalPulls) / stat.Pulls);
            var score = avgReward + bonus;
            if (score > bestScore)
            {
                bestScore = score;
                best = arm;
            }
        }

        log.LogDebug("Adaptive router chose {Provider} for intent {Intent} (score={Score:F3})", best.Name, intent, bestScore);
        return new RouterChoice(best.Name, best.Name);
    }

    public async Task RecordOutcomeAsync(string intent, string provider, string model, double reward, double latencyMs, bool failed, CancellationToken ct)
    {
        var stat = await db.RoutingStats.FirstOrDefaultAsync(
            s => s.Intent == intent && s.Provider == provider && s.Model == model, ct);

        if (stat is null)
        {
            stat = new RoutingStat
            {
                Id = Guid.CreateVersion7(),
                Intent = intent,
                Provider = provider,
                Model = model,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            db.RoutingStats.Add(stat);
        }

        stat.Pulls += 1;
        stat.TotalReward += Math.Clamp(reward, -1.0, 1.0);
        stat.TotalLatencyMs += latencyMs;
        if (failed) stat.Failures += 1;
        stat.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);
    }
}
