using System.ComponentModel;
using System.Security.Claims;
using System.Text.Json;
using Hope.Agent.Application.Abstractions;
using Hope.Agent.Application.Security;
using Hope.Agent.Application.Tools;
using Hope.Agent.Domain.Audit;
using Hope.Agent.Tools.Mcp;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace Hope.Agent.Api.Mcp;

/// <summary>
/// Expose tất cả IAgentTool của Hope.Agent ra ngoài qua giao thức MCP.
/// Bảo vệ bởi: Authentication (JWT / API Key) → McpPolicy → PromptShield → AuditTrail → AllowedTools.
/// </summary>
[McpServerToolType]
public sealed class HopeAgentMcpServer(
    IToolRegistry registry,
    IPromptShield shield,
    IServiceScopeFactory scopeFactory,
    IOptions<McpOptions> mcpOpts,
    IHttpContextAccessor http,
    ILogger<HopeAgentMcpServer> log)
{
    [McpServerTool(Name = "invoke_tool")]
    [Description("Invoke any Hope.Agent tool by name. Use list_tools first to discover available tools.")]
    public async Task<string> InvokeToolAsync(
        [Description("Name of the tool to invoke")] string toolName,
        [Description("JSON object matching the tool's input schema")] string argumentsJson,
        CancellationToken ct)
    {
        var caller = CallerName();
        var correlationId = $"mcp-{Guid.CreateVersion7()}";

        // 1. Tool allowlist check
        var allowed = mcpOpts.Value.AllowedTools;
        if (allowed is { Count: > 0 } && !allowed.Contains(toolName, StringComparer.OrdinalIgnoreCase))
        {
            log.LogWarning("MCP: caller '{Caller}' attempted blocked tool '{Tool}'.", caller, toolName);
            await WriteAuditAsync("mcp.tool.denied", toolName, correlationId,
                JsonSerializer.Serialize(new { reason = "not_in_allowlist", tool = toolName }), ct);
            return JsonSerializer.Serialize(new { error = "tool_not_allowed", tool_name = toolName });
        }

        // 2. Tool existence check
        var tool = registry.Find(toolName);
        if (tool is null)
        {
            log.LogWarning("MCP: tool '{Tool}' not found.", toolName);
            return JsonSerializer.Serialize(new { error = "tool_not_found", tool_name = toolName });
        }

        // 3. Prompt shield — inspect arguments for injection attacks
        var inspection = shield.Inspect(argumentsJson);
        if (!inspection.Allowed)
        {
            log.LogWarning("MCP: PromptShield blocked tool '{Tool}' from '{Caller}'. Reasons: {Reasons}",
                toolName, caller, string.Join(", ", inspection.Reasons));
            await WriteAuditAsync("mcp.shield.blocked", toolName, correlationId,
                JsonSerializer.Serialize(new { reasons = inspection.Reasons, caller }), ct);
            return JsonSerializer.Serialize(new
            {
                error = "blocked_by_security_policy",
                reasons = inspection.Reasons,
            });
        }

        // 4. Invoke — use sanitized arguments
        var ctx = new ToolInvocationContext(
            UserId: CallerId(),
            ConversationId: Guid.Empty,
            CorrelationId: correlationId);

        log.LogInformation("MCP invoke: caller='{Caller}' tool='{Tool}' correlationId={Id}",
            caller, toolName, correlationId);

        string result;
        try
        {
            result = await tool.InvokeAsync(inspection.SanitizedInput, ctx, ct);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "MCP tool '{Tool}' threw an exception.", toolName);
            await WriteAuditAsync("mcp.tool.error", toolName, correlationId,
                JsonSerializer.Serialize(new { error = ex.Message }), ct);
            return JsonSerializer.Serialize(new { error = "tool_execution_failed" });
        }

        // 5. Audit success
        await WriteAuditAsync("mcp.tool.invoked", toolName, correlationId,
            JsonSerializer.Serialize(new { caller, result_length = result.Length }), ct);

        return result;
    }

    [McpServerTool(Name = "list_tools")]
    [Description("List all available Hope.Agent tools with their schemas.")]
    public Task<string> ListToolsAsync(CancellationToken ct)
    {
        var allowed = mcpOpts.Value.AllowedTools;
        var tools = registry.All
            .Where(t => allowed is not { Count: > 0 } ||
                        allowed.Contains(t.Definition.Name, StringComparer.OrdinalIgnoreCase))
            .Select(t => new
            {
                name = t.Definition.Name,
                description = t.Definition.Description,
                schema = JsonDocument.Parse(t.Definition.ParametersJsonSchema).RootElement,
            });
        return Task.FromResult(JsonSerializer.Serialize(tools));
    }

    private string CallerName() =>
        http.HttpContext?.User.FindFirstValue(ClaimTypes.Name) ?? "unknown";

    private Guid CallerId()
    {
        var sub = http.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(sub, out var id) ? id : Guid.Empty;
    }

    private async Task WriteAuditAsync(string action, string toolName, string correlationId, string payload, CancellationToken ct)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var audit = scope.ServiceProvider.GetRequiredService<IAuditSink>();
            await audit.WriteAsync(new AuditEvent
            {
                Id = Guid.CreateVersion7(),
                OccurredAt = DateTimeOffset.UtcNow,
                UserId = CallerId(),
                Actor = "mcp_server",
                Action = action,
                ResourceType = "tool",
                ResourceId = toolName,
                CorrelationId = correlationId,
                PayloadJson = payload,
            }, ct);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "MCP: Failed to write audit event for action '{Action}'.", action);
        }
    }
}
