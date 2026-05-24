namespace Hope.Agent.Domain.Insights;

/// <summary>
/// Weekly LLM-generated summary of a user's conversations within a time window.
/// Full-text search uses Postgres <c>to_tsvector('simple', Content)</c> at query time
/// (see EfSessionInsightService); no stored tsvector column on the domain entity.
/// </summary>
public sealed class SessionSummary
{
    public Guid Id { get; init; }
    public Guid UserId { get; init; }
    public DateTimeOffset PeriodStart { get; init; }
    public DateTimeOffset PeriodEnd { get; init; }
    public int ConversationCount { get; init; }
    public required string Content { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}
