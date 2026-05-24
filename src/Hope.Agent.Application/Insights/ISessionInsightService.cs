using Hope.Agent.Domain.Insights;

namespace Hope.Agent.Application.Insights;

public interface ISessionInsightService
{
    /// <summary>Latest summaries for the user within the last <paramref name="days"/> days.</summary>
    Task<IReadOnlyList<SessionSummary>> RecentAsync(Guid userId, int days, CancellationToken ct);

    /// <summary>Full-text search across summary content (Postgres tsvector at query time).</summary>
    Task<IReadOnlyList<SessionSummary>> SearchAsync(Guid userId, string query, int take, CancellationToken ct);

    /// <summary>Generate a summary for the given user covering [periodStart, periodEnd).</summary>
    Task<SessionSummary?> GenerateAsync(Guid userId, DateTimeOffset periodStart, DateTimeOffset periodEnd, CancellationToken ct);
}

public sealed class SessionInsightOptions
{
    public const string Section = "SessionInsights";
    public bool Enabled { get; set; }
    /// <summary>Cadence of the periodic summarizer hosted service.</summary>
    public int IntervalDays { get; set; } = 7;
    /// <summary>Hour-of-day (UTC) to run the periodic summarizer.</summary>
    public int RunHourUtc { get; set; } = 2;
    public int MaxConversationsPerSummary { get; set; } = 50;
}
