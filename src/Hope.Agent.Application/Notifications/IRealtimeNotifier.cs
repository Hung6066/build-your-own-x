namespace Hope.Agent.Application.Notifications;

public sealed record AgentNotification(
    Guid Id,
    DateTimeOffset OccurredAt,
    string Channel,
    string Type,
    string Title,
    string Body,
    Guid? UserId = null,
    Dictionary<string, string>? Metadata = null);

/// <summary>
/// Realtime push to connected clients (SignalR/WebSocket). Implementations are non-blocking and best-effort.
/// </summary>
public interface IRealtimeNotifier
{
    Task BroadcastAsync(AgentNotification notification, CancellationToken ct);
    Task SendToUserAsync(Guid userId, AgentNotification notification, CancellationToken ct);
}
