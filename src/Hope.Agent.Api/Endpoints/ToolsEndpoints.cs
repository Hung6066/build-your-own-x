using System.Text.Json;
using Hope.Agent.Application.Tools;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Hope.Agent.Api.Endpoints;

/// <summary>
/// Exposes all registered IAgentTools in the OpenAI / MCP Atlas function-calling schema format.
/// Clients (Gemini Interactions API, MCP Atlas benchmarks, CI pipelines) can use this endpoint
/// to auto-discover every capability the agent exposes without hard-coding tool names.
/// </summary>
public static class ToolsEndpoints
{
    public static IEndpointRouteBuilder MapToolsEndpoints(this IEndpointRouteBuilder app)
    {
        // No auth — tool schemas are structural, not sensitive.
        // They're equivalent to an OpenAPI spec.
        var grp = app.MapGroup("/v1/tools").WithTags("Tools");

        grp.MapGet("", ([FromServices] IToolRegistry registry) =>
        {
            var tools = registry.All.Select(BuildToolSchema);
            return Results.Ok(new { tools });
        }).WithSummary("List all registered tools in MCP Atlas / OpenAI function-call format.");

        grp.MapGet("/{name}", ([FromRoute] string name, [FromServices] IToolRegistry registry) =>
        {
            var tool = registry.Find(name);
            return tool is null
                ? Results.NotFound(new { error = $"Tool '{name}' not found." })
                : Results.Ok(BuildToolSchema(tool));
        }).WithSummary("Get a single tool schema by exact name.");

        return app;
    }

    private static object BuildToolSchema(IAgentTool tool)
    {
        // Parse the stored JSON schema so it round-trips as a real object (not a string)
        JsonElement parameters;
        try
        {
            parameters = JsonSerializer.Deserialize<JsonElement>(tool.Definition.ParametersJsonSchema);
        }
        catch
        {
            parameters = JsonSerializer.Deserialize<JsonElement>("{\"type\":\"object\",\"properties\":{}}");
        }

        return new
        {
            type = "function",
            function = new
            {
                name = tool.Definition.Name,
                description = tool.Definition.Description,
                parameters,
            },
        };
    }
}
