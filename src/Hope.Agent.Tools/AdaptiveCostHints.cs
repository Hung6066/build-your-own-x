using System.Collections.Concurrent;
using Hope.Agent.Application.Tools;

namespace Hope.Agent.Tools;

/// <summary>
/// In-memory adaptive cost hints using Exponential Moving Average (α = 0.3).
/// Accumulates per-(doctorId, specialty) booking success rates at runtime.
/// Statistics reset on process restart — use a DB-backed implementation for persistence.
/// </summary>
internal sealed class AdaptiveCostHints : IOptimizationCostHints
{
    /// <summary>EMA smoothing factor: 0.3 gives ~3-4 samples for 1/e decay.</summary>
    private const double Alpha = 0.3;

    private readonly ConcurrentDictionary<string, double> _rates =
        new(StringComparer.OrdinalIgnoreCase);

    public Task RecordOutcomeAsync(string doctorId, string specialty, bool succeeded, CancellationToken ct)
    {
        var key = $"{doctorId}:{specialty}";
        var sample = succeeded ? 1.0 : 0.0;
        _rates.AddOrUpdate(
            key,
            addValue: sample,
            updateValueFactory: (_, prev) => prev + Alpha * (sample - prev));
        return Task.CompletedTask;
    }

    public Task<double> GetSuccessRateAsync(
        string doctorId,
        string specialty,
        double defaultRate = 0.85,
        CancellationToken ct = default)
    {
        var key = $"{doctorId}:{specialty}";
        var rate = _rates.TryGetValue(key, out var r) ? r : defaultRate;
        return Task.FromResult(rate);
    }
}
