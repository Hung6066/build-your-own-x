using Hope.Agent.Application.Agents.Multi;
using Hope.Agent.MultiAgent.Orchestration;
using Hope.Agent.MultiAgent.Roles;
using Microsoft.Extensions.DependencyInjection;

namespace Hope.Agent.MultiAgent;

public static class DependencyInjection
{
    public static IServiceCollection AddMultiAgent(this IServiceCollection services)
    {
        services.AddScoped<IAgentRole, SchedulingAgent>();
        services.AddScoped<IAgentRole, ClinicalAgent>();
        services.AddScoped<IAgentRole, BillingAgent>();
        services.AddScoped<IAgentRole, ComplianceAgent>();
        services.AddScoped<IAgentRole, EmergencyAgent>();
        services.AddScoped<IAgentRole, NotificationAgent>();
        services.AddScoped<IMultiAgentOrchestrator, ChiefMedicalAgent>();
        return services;
    }
}
