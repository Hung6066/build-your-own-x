namespace Hope.Agent.Infrastructure.Scheduling;

public sealed class ScheduledTaskOptions
{
    public const string Section = "ScheduledTasks";
    public bool Enabled { get; init; }
    public List<ScheduledTaskConfig> Tasks { get; init; } = [];
}

public sealed class ScheduledTaskConfig
{
    /// <summary>Human-readable task identifier used in logs and notification type.</summary>
    public string Name { get; init; } = "";
    /// <summary>UTC time to run (HH:mm). E.g. "00:00" = 7 AM ICT (UTC+7).</summary>
    public string TimeUtc { get; init; } = "00:00";
    /// <summary>Which days to run. Null or empty = every day.</summary>
    public DayOfWeek[]? DaysOfWeek { get; init; }
    /// <summary>Prompt forwarded to IAgentRuntime. Supports {date} and {dow} placeholders.</summary>
    public string Prompt { get; init; } = "";
    public string? AgentProfile { get; init; }
    /// <summary>Target UserId for the notification. Null = broadcast to all connected clients.</summary>
    public Guid? UserId { get; init; }
}
