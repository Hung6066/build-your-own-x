using Hope.Agent.Application.Agents;
using Hope.Agent.Application.Agents.Multi;
using Hope.Agent.Application.Agents.ReAct;
using Hope.Agent.Application.Governance;
using Hope.Agent.Application.Tools;
using Hope.Agent.MultiAgent.Governance;
using Hope.Agent.MultiAgent.Learning;
using Hope.Agent.MultiAgent.Memory;
using Hope.Agent.MultiAgent.Orchestration;
using Hope.Agent.MultiAgent.ReAct;
using Microsoft.Extensions.DependencyInjection;

namespace Hope.Agent.MultiAgent;

public static class DependencyInjection
{
    public static IServiceCollection AddMultiAgent(this IServiceCollection services,
        Action<GovernancePolicyOptions>? configureGovernance = null)
    {
        // Auto-discover toàn bộ IWorkflowModule trong assembly này và gọi RegisterServices.
        // Để thêm workflow mới: tạo class : IWorkflowModule trong Modules/WorkflowModules.cs,
        // không cần chỉnh sửa file này.
        ApplyWorkflowModules(services);

        services.AddScoped<IMultiAgentOrchestrator, ChiefMedicalAgent>();

        // ReAct loop — injectable into any IAgentRole that opts into iterative reasoning
        services.AddScoped<IReActLoop, ReActLoop>();

        // Tree of Thoughts — parallel branch exploration with LLM judge scoring
        services.AddScoped<ITreeOfThoughts, TreeOfThoughtsSearch>();

        // Cross-workflow patient memory — vector-backed recall via Qdrant
        services.AddScoped<IPatientMemoryService, PatientMemoryService>();

        // Feedback sink — records workflow outcomes into ISkillLibrary + IFeedbackStore
        services.AddScoped<IWorkflowOutcomeSink, WorkflowOutcomeSink>();

        // AGT Governance — Phase 1: policy enforcement + PHI scanning (singleton: holds GovernanceKernel)
        // Phase 3: AuditingGovernanceGate decorates AgtGovernanceGate, writing every policy denial
        // to audit_events so access-control decisions form part of the HIPAA compliance audit trail.
        services.AddOptions<GovernancePolicyOptions>()
            .BindConfiguration(GovernancePolicyOptions.SectionName);
        if (configureGovernance is not null)
            services.Configure(configureGovernance);
        services.AddSingleton<AgtGovernanceGate>();                         // concrete inner
        services.AddSingleton<IGovernanceGate, AuditingGovernanceGate>();   // decorated outer

        return services;
    }

    /// <summary>
    /// Tìm toàn bộ <see cref="IWorkflowModule"/> trong assembly hiện tại,
    /// khởi tạo và gọi <c>RegisterServices</c> cho từng module.
    /// </summary>
    private static void ApplyWorkflowModules(IServiceCollection services)
    {
        var moduleType = typeof(IWorkflowModule);
        foreach (var type in typeof(DependencyInjection).Assembly.GetTypes()
            .Where(t => moduleType.IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface))
        {
            var module = (IWorkflowModule)Activator.CreateInstance(type)!;
            module.RegisterServices(services);
        }
    }
}
