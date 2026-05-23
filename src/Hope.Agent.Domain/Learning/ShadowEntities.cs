namespace Hope.Agent.Domain.Learning;

/// <summary>Single shadow comparison: champion vs challenger answer on the same prompt.</summary>
public sealed class ShadowComparison
{
    public Guid Id { get; init; }
    public required string Intent { get; init; }
    public required string ChampionProvider { get; init; }
    public required string ChallengerProvider { get; init; }
    public double ChampionScore { get; init; }
    public double ChallengerScore { get; init; }
    public bool ChallengerWon { get; init; }
    public double LatencyDeltaMs { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>Active challenger config; only one active per intent.</summary>
public sealed class ChallengerConfig
{
    public Guid Id { get; init; }
    public required string Intent { get; init; }
    public required string ChallengerProvider { get; init; }
    public double TrafficFraction { get; set; }            // 0..1, fraction of traffic to mirror
    public int MinSamples { get; set; } = 50;
    public double PromotionWinRate { get; set; } = 0.55;
    public bool Active { get; set; } = true;
    public bool Promoted { get; set; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? PromotedAt { get; set; }
}
