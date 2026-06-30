using Hope.Agent.Application.Tools;
using Hope.Agent.Tools.Mcp;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Hope.Agent.Tools;

internal sealed class ToolRegistry(IEnumerable<IAgentTool> tools) : IToolRegistry
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, IAgentTool> _byName =
        new(tools.ToDictionary(t => t.Definition.Name, StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<IAgentTool> All => _byName.Values.ToList();
    public IAgentTool? Find(string name) => _byName.GetValueOrDefault(name);
    public void Register(IAgentTool tool) => _byName[tool.Definition.Name] = tool;
}

public static class DependencyInjection
{
    public static IServiceCollection AddAgentTools(this IServiceCollection services, IConfiguration configuration)
    {
        // Auto-discover toàn bộ IWorkflowModule trong assembly này và gọi RegisterServices.
        // Để thêm workflow mới: tạo class : IWorkflowModule trong Modules/WorkflowModules.cs,
        // không cần chỉnh sửa file này.
        ApplyWorkflowModules(services);

        services.AddSingleton<IToolRegistry, ToolRegistry>();

        // Adaptive MCMF cost hints — accumulates booking success rates at runtime
        services.TryAddSingleton<IOptimizationCostHints, AdaptiveCostHints>();

        services.Configure<ClinicalIntegrationOptions>(configuration.GetSection(ClinicalIntegrationOptions.Section));
        services.AddHttpClient("clinical-integrations", c => c.Timeout = TimeSpan.FromSeconds(30));

        // MCP client: kết nối tới các MCP server bên ngoài
        services.Configure<McpOptions>(configuration.GetSection("Mcp"));
        if (configuration.GetValue("Runtime:EnableHostedServices", true))
            services.AddHostedService<McpToolDiscoveryService>();

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
