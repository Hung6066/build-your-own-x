namespace Hope.Agent.Application.Subagents;

public sealed record SubagentSpec(string Profile, string SystemPromptHint);

public sealed record SubagentRequest(
    Guid UserId,
    string Question,
    IReadOnlyList<SubagentSpec> Specs,
    string? CorrelationId = null);

public sealed record SubagentBranchResult(
    string Profile,
    string Reply,
    int PromptTokens,
    int CompletionTokens,
    TimeSpan Duration,
    string? Error = null);

public sealed record SubagentAggregateResult(
    string Aggregate,
    IReadOnlyList<SubagentBranchResult> Branches,
    TimeSpan TotalDuration);

public interface ISubagentPool
{
    Task<SubagentAggregateResult> FanOutAsync(SubagentRequest request, CancellationToken ct);
}

public sealed class SubagentPoolOptions
{
    public const string Section = "Subagents";
    public bool Enabled { get; set; }
    public int MaxParallelism { get; set; } = 5;
    public int PerBranchTimeoutSeconds { get; set; } = 60;
    public string AggregationPrompt { get; set; } =
        "You are aggregating opinions from multiple specialist clinical sub-agents. " +
        "Produce a concise differential diagnosis: consensus first, then dissenting views, " +
        "then a single recommended next action. Never invent details that no branch reported.";
}
