using Hope.Agent.Application.LLM;
using Hope.Agent.Application.Security;
using Hope.Agent.Application.Tools;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;

namespace Hope.Agent.Tools.Mcp;

/// <summary>
/// BackgroundService chạy khi app khởi động.
/// Kết nối tới từng MCP server, liệt kê tools, đăng ký vào IToolRegistry.
/// Các tool MCP sẽ sẵn sàng trước khi app nhận request đầu tiên.
/// </summary>
internal sealed class McpToolDiscoveryService(
    IToolRegistry registry,
    IOptions<McpOptions> opts,
    ISsrfGuard ssrfGuard,
    ILogger<McpToolDiscoveryService> log) : BackgroundService
{
    private readonly List<McpClient> _clients = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var servers = opts.Value.Servers;
        if (servers.Count == 0)
        {
            log.LogDebug("MCP: No servers configured — skipping discovery.");
            return;
        }

        foreach (var server in servers)
        {
            try
            {
                var client = await ConnectAsync(server, ssrfGuard, stoppingToken);
                _clients.Add(client);

                var tools = await client.ListToolsAsync(cancellationToken: stoppingToken);
                var count = 0;

                foreach (var tool in tools)
                {
                    var schema = tool.JsonSchema.ValueKind == System.Text.Json.JsonValueKind.Undefined
                        ? """{"type":"object","properties":{}}"""
                        : tool.JsonSchema.GetRawText();

                    var definition = new ToolDefinition(
                        Name: tool.Name,
                        Description: $"[MCP:{server.Name}] {tool.Description}",
                        ParametersJsonSchema: schema);

                    registry.Register(new McpToolAdapter(client, server.Name, definition));
                    count++;
                }

                log.LogInformation("MCP: Connected to '{Server}', registered {Count} tools.", server.Name, count);
            }
            catch (Exception ex) when (server.Optional)
            {
                log.LogWarning(ex, "MCP: Could not connect to optional server '{Server}'. Skipping.", server.Name);
            }
        }
    }

    private static async Task<McpClient> ConnectAsync(McpServerEntry server, ISsrfGuard ssrfGuard, CancellationToken ct)
    {
        if (server.Transport.Equals("stdio", StringComparison.OrdinalIgnoreCase))
        {
            var transport = new StdioClientTransport(new StdioClientTransportOptions
            {
                Command = server.Command ?? throw new InvalidOperationException(
                    $"MCP server '{server.Name}': Command is required for stdio transport."),
                Arguments = server.Args,
            });
            return await McpClient.CreateAsync(transport, cancellationToken: ct);
        }

        // HTTP / SSE (default)
        var rawEndpoint = server.Endpoint ?? throw new InvalidOperationException(
            $"MCP server '{server.Name}': Endpoint is required for HTTP transport.");

        // ── NemoClaw SSRF guard: validate MCP endpoint before connecting ──
        var ssrf = ssrfGuard.Validate(rawEndpoint);
        if (!ssrf.Safe)
            throw new InvalidOperationException(
                $"MCP server '{server.Name}': endpoint blocked by SSRF guard — {ssrf.BlockReason}");

        var httpTransport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(rawEndpoint),
        });
        return await McpClient.CreateAsync(httpTransport, cancellationToken: ct);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var client in _clients)
        {
            try { await client.DisposeAsync(); }
            catch { /* best-effort */ }
        }
        await base.StopAsync(cancellationToken);
    }
}
