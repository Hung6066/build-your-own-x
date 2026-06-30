namespace Hope.Agent.Tools.Mcp;

/// <summary>Cấu hình danh sách MCP server mà Hope.Agent sẽ kết nối tới.</summary>
public sealed class McpOptions
{
    public List<McpServerEntry> Servers { get; set; } = [];

    /// <summary>
    /// SHA-256 hex hashes của các API key hợp lệ cho MCP endpoint (legacy — không có lifecycle).
    /// Tạo hash: <c>echo -n "your-key" | sha256sum</c>
    /// hoặc gọi <c>ApiKeyAuthHandler.HashKey("your-key")</c>.
    /// Ưu tiên dùng <see cref="ApiKeys"/> để có expiry/revocation.
    /// </summary>
    public List<string> ApiKeyHashes { get; set; } = [];

    /// <summary>
    /// API keys có lifecycle (rotation/expiry/revocation). Hỗ trợ hot-reload qua
    /// IOptionsMonitor: thay đổi config (thêm key mới, set ExpiresAt/Revoked cho key cũ)
    /// có hiệu lực ngay không cần restart — đây là cơ chế rotation zero-downtime.
    /// </summary>
    public List<ApiKeyEntry> ApiKeys { get; set; } = [];

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

/// <summary>Một API key có vòng đời quản lý được (rotation/expiry/revocation).</summary>
public sealed class ApiKeyEntry
{
    /// <summary>Tên định danh của key (ghi vào claim/audit — KHÔNG phải bí mật).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>SHA-256 hex của raw key.</summary>
    public string Hash { get; set; } = string.Empty;

    /// <summary>Thời điểm hết hạn (UTC). Null = không hết hạn.</summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>true = key bị thu hồi ngay lập tức.</summary>
    public bool Revoked { get; set; }
}
