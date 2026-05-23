using Hope.Agent.Application.Tools;
using Hope.Agent.Tools.Mcp;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
        services.AddScoped<IAgentTool, PatientLookupTool>();
        services.AddScoped<IAgentTool, AppointmentScheduleTool>();
        services.AddScoped<IAgentTool, InsuranceVerifyTool>();
        services.AddScoped<IAgentTool, ClinicalGuidelineSearchTool>();
        services.AddSingleton<IToolRegistry, ToolRegistry>();

        // MCP client: kết nối tới các MCP server bên ngoài
        services.Configure<McpOptions>(configuration.GetSection("Mcp"));
        services.AddHostedService<McpToolDiscoveryService>();

        return services;
    }
}
