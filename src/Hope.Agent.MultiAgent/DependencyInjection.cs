using Hope.Agent.Application.Agents.Multi;
using Hope.Agent.Application.Tools;
using Hope.Agent.MultiAgent.Orchestration;
using Microsoft.Extensions.DependencyInjection;

namespace Hope.Agent.MultiAgent;

public static class DependencyInjection
{
    public static IServiceCollection AddMultiAgent(this IServiceCollection services)
    {
        // Auto-discover toàn bộ IWorkflowModule trong assembly này và gọi RegisterServices.
        // Để thêm workflow mới: tạo class : IWorkflowModule trong Modules/WorkflowModules.cs,
        // không cần chỉnh sửa file này.
        ApplyWorkflowModules(services);

        services.AddScoped<IMultiAgentOrchestrator, ChiefMedicalAgent>();
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
