namespace Hope.Agent.Domain.Security;

public sealed class ApiKeyRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Hash { get; set; } = string.Empty;
    public string Scope { get; set; } = "hope-agent:mcp";
    public bool Revoked { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? RotatedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public string? CreatedBy { get; set; }
    public string? RevokedBy { get; set; }
    public string? Reason { get; set; }
}
