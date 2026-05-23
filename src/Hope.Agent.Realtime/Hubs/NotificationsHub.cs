using Hope.Agent.Application.Notifications;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Hope.Agent.Realtime.Hubs;

[Authorize]
public sealed class NotificationsHub : Hub
{
    public override Task OnConnectedAsync()
    {
        var sub = Context.User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                  ?? Context.User?.FindFirst("sub")?.Value;
        if (Guid.TryParse(sub, out var uid))
        {
            return Groups.AddToGroupAsync(Context.ConnectionId, GroupForUser(uid));
        }
        return Task.CompletedTask;
    }

    internal static string GroupForUser(Guid userId) => $"user:{userId:N}";
}

internal sealed class SignalRRealtimeNotifier(IHubContext<NotificationsHub> hub) : IRealtimeNotifier
{
    public Task BroadcastAsync(AgentNotification notification, CancellationToken ct) =>
        hub.Clients.All.SendAsync("notification", notification, ct);

    public Task SendToUserAsync(Guid userId, AgentNotification notification, CancellationToken ct) =>
        hub.Clients.Group(NotificationsHub.GroupForUser(userId)).SendAsync("notification", notification, ct);
}
