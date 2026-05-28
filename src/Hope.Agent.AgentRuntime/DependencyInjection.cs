using Hope.Agent.AgentRuntime.Roles;
using Hope.Agent.AgentRuntime.Subagents;
using Hope.Agent.Application.Agents;
using Hope.Agent.Application.Agents.Multi;
using Hope.Agent.Application.Caching;
using Hope.Agent.Application.Plans;
using Hope.Agent.Application.Subagents;
using Hope.Agent.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Hope.Agent.AgentRuntime;

public static class DependencyInjection
{
    public static IServiceCollection AddAgentRuntime(this IServiceCollection services, IConfiguration cfg)
    {
        services.Configure<AgentRuntimeOptions>(cfg.GetSection("AgentRuntime"));
        services.Configure<SubagentPoolOptions>(cfg.GetSection(SubagentPoolOptions.Section));
        services.AddSingleton<IClock, SystemClock>();
        services.AddScoped<Hope.Agent.AgentRuntime.Security.SandboxedToolExecutor>();
        services.AddScoped<IAgentRuntime, AgentOrchestrator>();
        services.AddSingleton<ISubagentPool, ParallelSubagentPool>();

        // ── Tier S defaults: no-op caches & plan tracker. Swap to Redis-backed
        //    implementations in Infrastructure to activate semantic cache, tool
        //    result cache, and persistent agent plan tracker.
        services.AddSingleton<ISemanticChatCache, NoOpSemanticChatCache>();
        services.AddSingleton<IToolResultCache, NoOpToolResultCache>();
        services.AddSingleton<IAgentPlanTracker, NoOpAgentPlanTracker>();

        // ── Clinical workflow agent roles ────────────────────────────────────
        services.AddScoped<IAgentRole, SchedulingAgentRole>();
        services.AddScoped<IAgentRole, MedicalSummaryAgentRole>();
        services.AddScoped<IAgentRole, InsuranceVerificationAgentRole>();
        services.AddScoped<IAgentRole, ReminderAgentRole>();
        services.AddScoped<IAgentRole, AuditReportAgentRole>();

        return services;
    }
}
