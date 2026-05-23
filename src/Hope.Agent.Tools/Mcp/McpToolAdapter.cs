using System.Text.Json;
using Hope.Agent.Application.LLM;
using Hope.Agent.Application.Tools;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Hope.Agent.Tools.Mcp;

/// <summary>
/// Wraps một tool từ MCP server thành IAgentTool.
/// AgentOrchestrator không cần biết đây là MCP tool hay native tool.
/// </summary>
internal sealed class McpToolAdapter(
    McpClient client,
    string serverName,
    ToolDefinition definition) : IAgentTool
{
    public ToolDefinition Definition { get; } = definition;

    public async Task<string> InvokeAsync(
        string argumentsJson,
        ToolInvocationContext context,
        CancellationToken ct)
    {
        // Parse argumentsJson → Dictionary<string, object?> để truyền vào MCP
        Dictionary<string, object?> args = [];
        if (!string.IsNullOrWhiteSpace(argumentsJson))
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            args = doc.RootElement.EnumerateObject()
                      .ToDictionary(p => p.Name, p => (object?)p.Value.Clone());
        }

        // Gọi MCP server
        var result = await client.CallToolAsync(
            Definition.Name,
            args,
            cancellationToken: ct);

        // Gộp tất cả TextContent thành chuỗi
        var texts = result.Content
            .OfType<TextContentBlock>()
            .Select(c => c.Text ?? string.Empty)
            .ToList();

        return texts.Count == 1
            ? texts[0]   // single text → trả thẳng (thường đã là JSON)
            : JsonSerializer.Serialize(new { server = serverName, contents = texts });
    }
}
