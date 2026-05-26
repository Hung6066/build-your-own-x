using Hope.Agent.Application.Agents.Multi;
using Hope.Agent.Application.Tools;

namespace Hope.Agent.Application.Agents.ReAct;

/// <summary>
/// Tree of Thoughts search (Yao et al., 2023):
/// generate <see cref="ToTOptions.BranchCount"/> independent reasoning branches in parallel,
/// score each with an LLM judge, and return the highest-scoring answer.
/// </summary>
public interface ITreeOfThoughts
{
    Task<ToTResult> RunAsync(
        AgentTask task,
        IReadOnlyList<IAgentTool> availableTools,
        ToTOptions? options = null,
        CancellationToken ct = default);
}

public sealed class ToTOptions
{
    /// <summary>Number of independent branches to explore in parallel.</summary>
    public int BranchCount { get; init; } = 3;

    /// <summary>Maximum ReAct iterations per branch.</summary>
    public int MaxStepsPerBranch { get; init; } = 3;

    /// <summary>Higher temperature increases branch diversity.</summary>
    public float Temperature { get; init; } = 0.7f;

    /// <summary>Optional extra context appended to each branch's system prompt.</summary>
    public string? SystemPromptSuffix { get; init; }
}

public sealed record ToTResult(
    bool Success,
    string BestAnswer,
    IReadOnlyList<ToTBranch> Branches,
    int WinnerBranchIndex,
    double WinnerScore);

public sealed record ToTBranch(
    int Index,
    string Answer,
    double Score,
    bool Passed,
    string JudgeReasoning,
    int StepCount);
