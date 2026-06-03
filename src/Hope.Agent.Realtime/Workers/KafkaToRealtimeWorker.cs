using System.Text.Json;
using Hope.Agent.Application.Eventing;
using Hope.Agent.Application.Notifications;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Hope.Agent.Realtime.Workers;

/// <summary>
/// Subscribes to agent.* Kafka topics and fans messages out to SignalR clients.
/// Decouples the API from realtime delivery — the API only publishes to Kafka.
/// </summary>
internal sealed class KafkaToRealtimeWorker(
    IEventConsumer consumer,
    IServiceScopeFactory scopes,
    ILogger<KafkaToRealtimeWorker> log) : BackgroundService
{
    private static readonly string[] Topics =
    [
        "agent.notifications",
        "agent.task.created",
        "agent.task.completed",
        "agent.role.completed",
    ];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        log.LogInformation("Kafka→Realtime worker started, topics={Topics}", string.Join(',', Topics));
        var delay = TimeSpan.FromSeconds(5);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await foreach (var env in consumer.ConsumeAsync(Topics, "realtime-fanout", stoppingToken))
                {
                    try
                    {
                        await using var scope = scopes.CreateAsyncScope();
                        var notifier = scope.ServiceProvider.GetRequiredService<IRealtimeNotifier>();
                        var notification = env.Topic == "agent.notifications"
                            ? JsonSerializer.Deserialize<AgentNotification>(env.PayloadJson)
                            : ToSyntheticNotification(env);
                        if (notification is null) continue;
                        if (notification.UserId is { } uid) await notifier.SendToUserAsync(uid, notification, stoppingToken);
                        else await notifier.BroadcastAsync(notification, stoppingToken);
                    }
                    catch (Exception ex)
                    {
                        log.LogError(ex, "Failed to relay event {Topic} key={Key}", env.Topic, env.Key);
                    }
                }
                delay = TimeSpan.FromSeconds(5); // reset on clean exit
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Kafka→Realtime worker connection failed; retrying in {Delay}s", delay.TotalSeconds);
                try { await Task.Delay(delay, stoppingToken); } catch (OperationCanceledException) { break; }
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 120));
            }
        }
    }

    private static AgentNotification ToSyntheticNotification(EventEnvelope env) => new(
        Id: Guid.CreateVersion7(),
        OccurredAt: DateTimeOffset.UtcNow,
        Channel: "agent-events",
        Type: env.Topic,
        Title: env.Topic,
        Body: env.PayloadJson,
        UserId: null,
        Metadata: new Dictionary<string, string> { ["key"] = env.Key });
}
