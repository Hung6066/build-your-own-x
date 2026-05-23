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
}
