using Hope.Agent.Domain.Learning;

namespace Hope.Agent.Application.Learning;

public interface IFeedbackStore
{
    Task RecordAsync(Feedback feedback, CancellationToken ct);
    Task<IReadOnlyList<Feedback>> RecentByConversationAsync(Guid conversationId, int take, CancellationToken ct);
}

public interface ISkillLibrary
{
    Task RecordSuccessAsync(LearnedSkill skill, CancellationToken ct);
    Task<IReadOnlyList<LearnedSkill>> RetrieveByIntentAsync(string intent, int topK, CancellationToken ct);
    Task IncrementUsageAsync(Guid skillId, double rewardDelta, CancellationToken ct);
}

public interface IAdaptiveRouter
{
    /// <summary>Picks a chat provider based on stored bandit statistics for the given intent.</summary>
    Task<RouterChoice> SelectChatAsync(string intent, CancellationToken ct);
    Task RecordOutcomeAsync(string intent, string provider, string model, double reward, double latencyMs, bool failed, CancellationToken ct);
}

public sealed record RouterChoice(string Provider, string Model);

public interface IReflector
{
    Task<ReflectionResult> CritiqueAndRefineAsync(string userMessage, string draftAnswer, CancellationToken ct);
}

public sealed record ReflectionResult(double Score, string RefinedAnswer, string Critique);

public interface IJudge
{
    Task<JudgeVerdict> ScoreAsync(string userMessage, string candidateAnswer, string? referenceAnswer, CancellationToken ct);
}

public sealed record JudgeVerdict(double Score, bool Passed, string Reasoning);

public interface IEvaluationHarness
{
    Task<EvalRun> RunSuiteAsync(string suiteName, CancellationToken ct);
    Task<IReadOnlyList<EvalRun>> RecentRunsAsync(int take, CancellationToken ct);
    Task<EvalMetricSummary> GetMetricsAsync(string suite, int days, CancellationToken ct);

    /// <summary>
    /// Returns score-over-time for a suite. Each point includes a delta vs the previous run
    /// so callers can immediately see whether the agent is improving or regressing.
    /// </summary>
    Task<IReadOnlyList<EvalTrendPoint>> GetTrendAsync(string suite, int days, CancellationToken ct);

    /// <summary>
    /// Runs a Co-Scientist-style Elo tournament between the two most recent runs of the suite.
    /// Each matching test-case is treated as a matchup; per-case scores determine the winner.
    /// Elo ratings on both EvalRun rows are updated and persisted.
    /// </summary>
    Task<EloTournamentResult> RunEloTournamentAsync(string suite, CancellationToken ct);

    /// <summary>Returns completed runs for the suite, ordered by EloRating descending.</summary>
    Task<IReadOnlyList<EvalRun>> GetLeaderboardAsync(string suite, int take, CancellationToken ct);
}

/// <summary>Result of a pairwise Elo tournament between two consecutive eval runs.</summary>
public sealed record EloTournamentResult(
    Guid WinnerId,
    Guid LoserId,
    double WinnerEloAfter,
    double LoserEloAfter,
    int TotalMatchups,
    int WinnerWins,
    int Draws);

/// <summary>One data point in the evaluation trend series.</summary>
public sealed record EvalTrendPoint(
    Guid RunId,
    DateTimeOffset RunAt,
    int Total,
    int Passed,
    int Failed,
    double AvgScore,
    /// <summary>Change in AvgScore vs the immediately preceding run. Null for the first data point.</summary>
    double? DeltaScore);

public sealed record EvalMetricSummary(
    string Suite,
    int Runs,
    int TotalCases,
    double TaskSuccessRate,
    double HallucinationRate,
    double ToolCallAccuracy,
    double Faithfulness,
    double AvgJudgeScore,
    double LatencyP95Ms,
    double CostPerSuccessUsd);

/// <summary>CRUD store for DB-backed evaluation test cases.</summary>
public interface IEvalCaseStore
{
    /// <summary>All active cases for the given suite, ordered by CreatedAt.</summary>
    Task<IReadOnlyList<EvalCase>> GetBySuiteAsync(string suite, CancellationToken ct);

    Task<EvalCase> AddAsync(EvalCase evalCase, CancellationToken ct);

    /// <summary>Soft-deletes (sets Active=false). Returns false when ID not found.</summary>
    Task<bool> DeleteAsync(Guid id, CancellationToken ct);
}
