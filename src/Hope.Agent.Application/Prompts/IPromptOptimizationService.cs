namespace Hope.Agent.Application.Prompts;

public sealed class PromptOptimizationOptions
{
    public const string SectionName = "PromptOptimization";
    public bool Enabled { get; init; } = true;
    public string[] DefaultSuites { get; init; } = ["default"];
    public int CandidateCount { get; init; } = 3;
    public double MinPromotionDelta { get; init; } = 0.03;
    public bool AutoPromote { get; init; }
    public int IntervalHours { get; init; } = 24;
}

public sealed record PromptOptimizationResult(
    string PromptName,
    string Suite,
    string BaselineVersion,
    double BaselineScore,
    string BestCandidateVersion,
    double BestCandidateScore,
    bool Promoted,
    IReadOnlyList<PromptCandidateScore> Candidates);

public sealed record PromptCandidateScore(string Version, double Score, bool Passed, string Reason);

public interface IPromptOptimizationService
{
    Task<PromptOptimizationResult> OptimizeAsync(string promptName, string suite, bool? autoPromote, CancellationToken ct);
}
