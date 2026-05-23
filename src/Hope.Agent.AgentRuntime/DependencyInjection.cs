using Hope.Agent.Application.Agents;
using Hope.Agent.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Hope.Agent.AgentRuntime;

public static class DependencyInjection
{
    public static IServiceCollection AddAgentRuntime(this IServiceCollection services, IConfiguration cfg)
    {
        services.Configure<AgentRuntimeOptions>(cfg.GetSection("AgentRuntime"));
        services.AddSingleton<IClock, SystemClock>();
        services.AddScoped<IAgentRuntime, AgentOrchestrator>();
        return services;
    }
}
