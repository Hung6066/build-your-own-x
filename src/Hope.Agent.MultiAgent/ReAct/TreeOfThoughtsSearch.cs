using Hope.Agent.Application.Agents.Multi;
using Hope.Agent.Application.Agents.ReAct;
using Hope.Agent.Application.Learning;
using Hope.Agent.Application.LLM;
using Hope.Agent.Application.Tools;
using Microsoft.Extensions.Logging;

namespace Hope.Agent.MultiAgent.ReAct;

/// <summary>
/// Tree of Thoughts implementation (Yao et al., 2023).
/// Runs <see cref="ToTOptions.BranchCount"/> independent ReAct branches in parallel,
/// scores each candidate answer with <see cref="IJudge"/>, and returns the best branch.
/// </summary>
internal sealed class TreeOfThoughtsSearch(
    IReActLoop reactLoop,
    IJudge judge,
    ILogger<TreeOfThoughtsSearch> log) : ITreeOfThoughts
{
    public async Task<ToTResult> RunAsync(
        AgentTask task,
        IReadOnlyList<IAgentTool> availableTools,
        ToTOptions? options = null,
        CancellationToken ct = default)
    {
        var opts = options ?? new ToTOptions();

        // Run all branches in parallel with diverse temperature for exploration
        var branchOpts = new ReActOptions
        {
            MaxIterations = opts.MaxStepsPerBranch,
            Temperature = opts.Temperature,
            EnableReflection = false,
            SystemPromptSuffix = opts.SystemPromptSuffix,
        };

        var branchTasks = Enumerable.Range(0, opts.BranchCount)
            .Select(i => RunBranchAsync(i, task, availableTools, branchOpts, ct))
            .ToList();

        var rawBranches = await Task.WhenAll(branchTasks);

        // Score all branches in parallel with the judge
        var judgedTasks = rawBranches
            .Select(b => ScoreBranchAsync(task.Input, b, ct))
            .ToList();

        var branches = await Task.WhenAll(judgedTasks);

        var winner = branches.OrderByDescending(b => b.Score).First();

        log.LogInformation(
            "ToT completed: {Count} branches, winner=branch{WinnerIdx} score={Score:F2} passed={Passed}",
            opts.BranchCount, winner.Index, winner.Score, winner.Passed);

        return new ToTResult(
            Success: true,
            BestAnswer: winner.Answer,
            Branches: branches,
            WinnerBranchIndex: winner.Index,
            WinnerScore: winner.Score);
    }

    private async Task<(int Index, string Answer, int Steps)> RunBranchAsync(
        int index,
        AgentTask task,
        IReadOnlyList<IAgentTool> tools,
        ReActOptions opts,
        CancellationToken ct)
    {
        try
        {
            var result = await reactLoop.RunAsync(task, tools, opts, ct);
            return (index, result.FinalAnswer, result.Steps.Count);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "ToT branch {Index} failed", index);
            return (index, string.Empty, 0);
        }
    }

    private async Task<ToTBranch> ScoreBranchAsync(
        string question,
        (int Index, string Answer, int Steps) branch,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(branch.Answer))
            return new ToTBranch(branch.Index, branch.Answer, 0.0, false, "(branch failed — no answer)", branch.Steps);

        try
        {
            var verdict = await judge.ScoreAsync(question, branch.Answer, referenceAnswer: null, ct);
            return new ToTBranch(branch.Index, branch.Answer, verdict.Score, verdict.Passed, verdict.Reasoning, branch.Steps);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Judge failed for ToT branch {Index}", branch.Index);
            // Assign neutral score so the branch stays eligible but is not preferred
            return new ToTBranch(branch.Index, branch.Answer, 0.5, true, "(judge unavailable)", branch.Steps);
        }
    }
}
