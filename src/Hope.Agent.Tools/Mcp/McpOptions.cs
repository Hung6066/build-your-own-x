namespace Hope.Agent.Tools.Mcp;

/// <summary>Cấu hình danh sách MCP server mà Hope.Agent sẽ kết nối tới.</summary>
public sealed class McpOptions
{
    public List<McpServerEntry> Servers { get; set; } = [];

    /// <summary>
    /// SHA-256 hex hashes của các API key hợp lệ cho MCP endpoint.
    /// Tạo hash: <c>echo -n "your-key" | sha256sum</c>
    /// hoặc gọi <c>ApiKeyAuthHandler.HashKey("your-key")</c>.
    /// </summary>
    public List<string> ApiKeyHashes { get; set; } = [];

    /// <summary>
    /// Danh sách tool được phép gọi qua MCP endpoint.
    /// Danh sách rỗng = cho phép tất cả tool (không khuyến nghị cho production).
    /// </summary>
    public List<string> AllowedTools { get; set; } = [];

    /// <summary>Rate limit cho MCP endpoint (requests per minute). Default: 30.</summary>
    public int RateLimitPerMinute { get; set; } = 30;
}

public sealed class McpServerEntry
{
    /// <summary>Tên định danh (dùng trong log).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>"sse" hoặc "stdio".</summary>
    public string Transport { get; set; } = "sse";

    /// <summary>URL endpoint (dùng khi Transport = "sse").</summary>
    public string? Endpoint { get; set; }

    /// <summary>Executable (dùng khi Transport = "stdio").</summary>
    public string? Command { get; set; }

    /// <summary>Arguments cho Command (dùng khi Transport = "stdio").</summary>
    public List<string> Args { get; set; } = [];

    /// <summary>Nếu true, lỗi kết nối không crash app — chỉ log warning.</summary>
    public bool Optional { get; set; } = true;
}
