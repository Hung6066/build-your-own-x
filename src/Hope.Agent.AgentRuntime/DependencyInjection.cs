using Hope.Agent.AgentRuntime.Roles;
using Hope.Agent.AgentRuntime.Subagents;
using Hope.Agent.Application.Agents;
using Hope.Agent.Application.Agents.Multi;
using Hope.Agent.Application.Billing;
using Hope.Agent.Application.Caching;
using Hope.Agent.Application.Context;
using Hope.Agent.Application.Locking;
using Hope.Agent.Application.Plans;
using Hope.Agent.Application.Autonomy;
using Hope.Agent.Application.Subagents;
using Hope.Agent.Application.Memory;
using Hope.Agent.Application.Security;
using Hope.Agent.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Hope.Agent.AgentRuntime;

public static class DependencyInjection
{
    /// <summary>No-op distributed lock used when Redis isn't available.</summary>
    private sealed class NoOpDistributedLock : IDistributedLock
    {
        public Task<ILockHandle?> AcquireAsync(string resource, TimeSpan expiry, CancellationToken ct)
            => Task.FromResult<ILockHandle?>(null);
    }

    /// <summary>No-op billing service — always allows, never records.</summary>
    private sealed class NoOpBillingService : ITenantBillingService
    {
        public Task<bool> CheckBudgetAsync(Guid tenantId, string model, int estimatedTokens, CancellationToken ct)
            => Task.FromResult(true);
        public Task RecordUsageAsync(UsageRecord record, CancellationToken ct) => Task.CompletedTask;
        public Task<TenantBudget> GetBudgetAsync(Guid tenantId, CancellationToken ct)
            => Task.FromResult(new TenantBudget(tenantId, decimal.MaxValue, 0, decimal.MaxValue, 0, DateTimeOffset.MaxValue));
        public Task SetBudgetCapAsync(Guid tenantId, decimal monthlyCapUsd, CancellationToken ct) => Task.CompletedTask;
    }

    public static IServiceCollection AddAgentRuntime(this IServiceCollection services, IConfiguration cfg)
    {
        services.Configure<AgentRuntimeOptions>(cfg.GetSection("AgentRuntime"));
        services.Configure<SubagentPoolOptions>(cfg.GetSection(SubagentPoolOptions.Section));
        services.AddSingleton<IClock, SystemClock>();
        services.AddScoped<Hope.Agent.AgentRuntime.Security.SandboxedToolExecutor>();
        services.AddScoped<Hope.Agent.Application.Tools.IToolExecutor>(sp =>
            sp.GetRequiredService<Hope.Agent.AgentRuntime.Security.SandboxedToolExecutor>());
        services.AddScoped(sp => new AgentRuntimeOptionalServices(
            sp.GetService<IClinicalContextProvider>(),
            sp.GetService<IMemoryConsolidator>(),
            sp.GetService<IMemoryReranker>(),
            sp.GetService<ITenantBillingService>(),
            sp.GetService<IDistributedLock>(),
            sp.GetService<IAutonomyDecisionService>(),
            sp.GetService<IContextProvenanceStore>()));
        services.AddScoped<IAgentRuntime, AgentOrchestrator>();
        services.AddSingleton<ISubagentPool, ParallelSubagentPool>();

        // ── Tier S defaults: no-op caches & plan tracker. Swap to Redis-backed
        //    implementations in Infrastructure to activate semantic cache, tool
        //    result cache, and persistent agent plan tracker.
        services.AddSingleton<ISemanticChatCache, NoOpSemanticChatCache>();
        services.AddSingleton<IToolResultCache, NoOpToolResultCache>();
        services.AddSingleton<IAgentPlanTracker, NoOpAgentPlanTracker>();

        // ── Billing & locking defaults: Infrastructure wires real implementations ──
        services.TryAddSingleton<ITenantBillingService, NoOpBillingService>();
        services.TryAddSingleton<IDistributedLock, NoOpDistributedLock>();

        // ── Clinical workflow agent roles ────────────────────────────────────
        services.AddScoped<IAgentRole, SchedulingAgentRole>();
        services.AddScoped<IAgentRole, MedicalSummaryAgentRole>();
        services.AddScoped<IAgentRole, InsuranceVerificationAgentRole>();
        services.AddScoped<IAgentRole, ReminderAgentRole>();
        services.AddScoped<IAgentRole, AuditReportAgentRole>();

        return services;
    }
}
