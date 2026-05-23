using Hope.Agent.Application.Notifications;
using Hope.Agent.Realtime.Hubs;
using Hope.Agent.Realtime.Workers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;

namespace Hope.Agent.Realtime;

public static class DependencyInjection
{
    public static IServiceCollection AddRealtime(this IServiceCollection services)
    {
        services.AddSignalR(o =>
        {
            o.EnableDetailedErrors = false;
            o.MaximumReceiveMessageSize = 64 * 1024;
        });
        services.AddSingleton<IRealtimeNotifier, SignalRRealtimeNotifier>();
        services.AddHostedService<KafkaToRealtimeWorker>();
        return services;
    }

    public static IEndpointRouteBuilder MapNotificationsHub(this IEndpointRouteBuilder app, string pattern = "/hubs/notifications")
    {
        app.MapHub<NotificationsHub>(pattern);
        return app;
    }
}
