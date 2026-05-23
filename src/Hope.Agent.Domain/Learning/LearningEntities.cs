namespace Hope.Agent.Domain.Learning;

public sealed class Feedback
{
    public Guid Id { get; init; }
    public Guid UserId { get; init; }
    public Guid ConversationId { get; init; }
    public int Rating { get; init; } // +1 thumbs up, -1 thumbs down, 0 neutral
    public string? Comment { get; init; }
    public string? Provider { get; init; }
    public string? Model { get; init; }
    public string? Intent { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

public sealed class LearnedSkill
{
    public Guid Id { get; init; }
    public required string Intent { get; init; }
    public required string Signature { get; init; }           // normalized prompt fingerprint
    public required string ToolSequenceJson { get; init; }    // ordered tool calls + args summary
    public required string AnswerTemplate { get; init; }      // distilled answer outline
    public double Reward { get; set; }                        // EMA of rewards
    public long UsageCount { get; set; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset LastUsed { get; set; }
}

public sealed class EvalRun
{
    public Guid Id { get; init; }
    public required string Suite { get; init; }
    public int Total { get; set; }
    public int Passed { get; set; }
    public int Failed { get; set; }
    public double AvgJudgeScore { get; set; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; set; }
    public required string ReportJson { get; set; }
}

public sealed class RoutingStat
{
    public Guid Id { get; init; }
    public required string Intent { get; init; }
    public required string Provider { get; init; }
    public required string Model { get; init; }
    public long Pulls { get; set; }
    public double TotalReward { get; set; }                   // sum of rewards in [-1, +1]
    public double TotalLatencyMs { get; set; }
    public long Failures { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
