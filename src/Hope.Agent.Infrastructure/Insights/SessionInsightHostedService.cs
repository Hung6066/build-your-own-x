using Hope.Agent.Application.Insights;
using Hope.Agent.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hope.Agent.Infrastructure.Insights;

/// <summary>
/// Wakes once an hour, and once per <c>IntervalDays</c> at the configured UTC hour it
/// generates a fresh <c>SessionSummary</c> for every user who had activity in that window.
/// </summary>
internal sealed class SessionInsightHostedService(
    IServiceScopeFactory scopes,
    IOptions<SessionInsightOptions> opts,
    ILogger<SessionInsightHostedService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = opts.Value;
        if (!o.Enabled)
        {
            log.LogInformation("Session-insight summarizer disabled.");
            return;
        }
        log.LogInformation("Session-insight summarizer started (every {Days}d at {Hour:00}:00 UTC).",
            o.IntervalDays, o.RunHourUtc);

        DateTimeOffset? lastRun = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromHours(1), stoppingToken); }
            catch (OperationCanceledException) { return; }

            var now = DateTimeOffset.UtcNow;
            if (now.Hour != o.RunHourUtc) continue;
            if (lastRun is { } prev && (now - prev).TotalDays < o.IntervalDays) continue;
            lastRun = now;

            try { await RunOnceAsync(o, now, stoppingToken); }
            catch (Exception ex) { log.LogError(ex, "Session-insight pass failed"); }
        }
    }

    private async Task RunOnceAsync(SessionInsightOptions o, DateTimeOffset now, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentDbContext>();
        var svc = scope.ServiceProvider.GetRequiredService<ISessionInsightService>();

        var periodEnd = now;
        var periodStart = now.AddDays(-o.IntervalDays);
        var userIds = await db.Conversations.AsNoTracking()
            .Where(c => c.UpdatedAt >= periodStart && c.UpdatedAt < periodEnd)
            .Select(c => c.UserId)
            .Distinct()
            .Take(500)
            .ToListAsync(ct);

        log.LogInformation("Session-insight: generating summaries for {Count} user(s).", userIds.Count);
        foreach (var userId in userIds)
        {
            try
            {
                await svc.GenerateAsync(userId, periodStart, periodEnd, ct);
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Session-insight: GenerateAsync failed for user {UserId}", userId);
            }
        }
    }
}
