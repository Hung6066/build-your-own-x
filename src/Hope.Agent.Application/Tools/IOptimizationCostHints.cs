namespace Hope.Agent.Application.Tools;

/// <summary>
/// Provides adaptive cost adjustments for the MCMF appointment optimizer
/// based on historical booking outcomes per (doctorId, specialty) pair.
/// </summary>
public interface IOptimizationCostHints
{
    /// <summary>Records whether a booking for a (doctorId, specialty) pair succeeded.</summary>
    Task RecordOutcomeAsync(string doctorId, string specialty, bool succeeded, CancellationToken ct);

    /// <summary>
    /// Returns the historical success rate [0.0, 1.0] for a (doctorId, specialty) pair.
    /// Returns <paramref name="defaultRate"/> when no history is available.
    /// </summary>
    Task<double> GetSuccessRateAsync(
        string doctorId,
        string specialty,
        double defaultRate = 0.85,
        CancellationToken ct = default);
}
