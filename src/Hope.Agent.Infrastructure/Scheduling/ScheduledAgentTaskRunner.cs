using System.Text.Json;
using Hope.Agent.Application.Agents;
using Hope.Agent.Application.Eventing;
using Hope.Agent.Application.Notifications;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hope.Agent.Infrastructure.Scheduling;

/// <summary>
/// Fires configured agent tasks at specific UTC times each day.
/// Wakes every minute and checks whether any task should run; uses per-task last-run
/// date tracking to prevent double-fire within the same UTC day.
/// </summary>
internal sealed class ScheduledAgentTaskRunner(
    IServiceScopeFactory scopes,
    IOptions<ScheduledTaskOptions> opts,
    ILogger<ScheduledAgentTaskRunner> log) : BackgroundService
{
    private readonly Dictionary<string, DateOnly> _lastRun = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!opts.Value.Enabled || opts.Value.Tasks.Count == 0)
        {
            log.LogInformation("Scheduled agent tasks are disabled or no tasks configured.");
            return;
        }

        log.LogInformation("Scheduled task runner started with {Count} task(s).", opts.Value.Tasks.Count);

        while (!stoppingToken.IsCancellationRequested)
        {
            // Sleep until the top of the next UTC minute.
            var now = DateTime.UtcNow;
            var nextMinute = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, DateTimeKind.Utc)
                .AddMinutes(1);
            try { await Task.Delay(nextMinute - now, stoppingToken); }
            catch (OperationCanceledException) { return; }

            var runAt = DateTime.UtcNow;
            var todayUtc = DateOnly.FromDateTime(runAt);
            var timeStr = runAt.ToString("HH:mm");

            foreach (var task in opts.Value.Tasks)
            {
                if (task.TimeUtc != timeStr) continue;
                if (task.DaysOfWeek is { Length: > 0 } days && !days.Contains(runAt.DayOfWeek)) continue;
                if (_lastRun.TryGetValue(task.Name, out var lastDate) && lastDate == todayUtc) continue;

                _lastRun[task.Name] = todayUtc;
                _ = RunTaskAsync(task, runAt, stoppingToken);
            }
        }
    }

    private async Task RunTaskAsync(ScheduledTaskConfig task, DateTime runAt, CancellationToken ct)
    {
        log.LogInformation("Running scheduled task: {Task} at {Time} UTC.", task.Name, runAt.ToString("HH:mm"));
        try
        {
            var prompt = task.Prompt
                .Replace("{date}", runAt.ToString("yyyy-MM-dd"))
                .Replace("{dow}", runAt.DayOfWeek.ToString());

            await using var scope = scopes.CreateAsyncScope();
            var runtime = scope.ServiceProvider.GetRequiredService<IAgentRuntime>();
            var publisher = scope.ServiceProvider.GetRequiredService<IEventPublisher>();

            var userId = task.UserId ?? Guid.Empty;
            var response = await runtime.RunAsync(
                new AgentRequest(userId, null, prompt, task.AgentProfile, $"sched:{task.Name}:{runAt:yyyyMMdd-HHmm}"),
                ct);

            var notification = new AgentNotification(
                Id: Guid.CreateVersion7(),
                OccurredAt: DateTimeOffset.UtcNow,
                Channel: "scheduled",
                Type: $"scheduled.{task.Name}",
                Title: $"[{task.Name}]",
                Body: response.Reply,
                UserId: task.UserId,
                Metadata: new Dictionary<string, string>
                {
                    ["task"] = task.Name,
                    ["date"] = runAt.ToString("yyyy-MM-dd"),
                    ["model"] = response.Model,
                    ["tokens"] = (response.PromptTokens + response.CompletionTokens).ToString(),
                });

            await publisher.PublishAsync(
                "agent.notifications",
                notification.Id.ToString(),
                JsonSerializer.Serialize(notification),
                ct);

            log.LogInformation("Scheduled task {Task} completed. Tokens={Tokens}.",
                task.Name, response.PromptTokens + response.CompletionTokens);
        }
        catch (OperationCanceledException) { /* host shutting down */ }
        catch (Exception ex)
        {
            log.LogError(ex, "Scheduled task {Task} failed.", task.Name);
        }
    }
}
