namespace Hope.Agent.Domain.Observability;

public sealed class AgentOpsHourlyMetric
{
    public Guid Id { get; set; }
    public Guid? TenantId { get; set; }
    public string AgentProfile { get; set; } = "unknown";
    public DateTimeOffset HourBucket { get; set; }
    public long AgentRuns { get; set; }
    public long ToolCalls { get; set; }
    public long ToolFailures { get; set; }
    public long Decisions { get; set; }
    public long ActionsQueued { get; set; }
    public long ActionsSucceeded { get; set; }
    public long ActionsFailed { get; set; }
    public double LatencyP95Ms { get; set; }
    public decimal CostUsd { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class TenantCostDaily
{
    public Guid Id { get; set; }
    public Guid? TenantId { get; set; }
    public DateOnly DayBucket { get; set; }
    public string AgentProfile { get; set; } = "all";
    public string Model { get; set; } = "all";
    public long Runs { get; set; }
    public decimal CostUsd { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class WorkflowSuccessDaily
{
    public Guid Id { get; set; }
    public Guid? TenantId { get; set; }
    public DateOnly DayBucket { get; set; }
    public string WorkflowName { get; set; } = "unknown";
    public long Started { get; set; }
    public long Succeeded { get; set; }
    public long Failed { get; set; }
    public double SuccessRate { get; set; }
    public double LatencyP95Ms { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class ScalePartitionPolicy
{
    public Guid Id { get; set; }
    public string TableName { get; set; } = string.Empty;
    public string PartitionKey { get; set; } = string.Empty;
    public string Strategy { get; set; } = "monthly";
    public int HotRetentionDays { get; set; }
    public int ArchiveAfterDays { get; set; }
    public bool Enabled { get; set; } = true;
    public DateTimeOffset UpdatedAt { get; set; }
}
