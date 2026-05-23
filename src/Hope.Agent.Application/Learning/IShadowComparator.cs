using Hope.Agent.Domain.Learning;

namespace Hope.Agent.Application.Learning;

public interface IShadowComparator
{
    /// <summary>Returns the active challenger for an intent, or null if none.</summary>
    Task<ChallengerConfig?> GetActiveChallengerAsync(string intent, CancellationToken ct);

    /// <summary>Records a head-to-head comparison; auto-promotes when criteria are met.</summary>
    Task RecordAsync(ShadowComparison comparison, CancellationToken ct);

    Task<IReadOnlyList<ShadowComparison>> RecentAsync(string intent, int take, CancellationToken ct);
    Task<ChallengerConfig> UpsertChallengerAsync(ChallengerConfig config, CancellationToken ct);
}
