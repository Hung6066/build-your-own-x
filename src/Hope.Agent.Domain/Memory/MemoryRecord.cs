namespace Hope.Agent.Domain.Memory;

public sealed class MemoryRecord
{
    public Guid Id { get; init; }
    public Guid? TenantId { get; init; }
    public Guid UserId { get; init; }
    public Guid? ConversationId { get; init; }
    public MemoryKind Kind { get; init; }
    public string Content { get; init; } = string.Empty;
    public string? Source { get; init; }
    public float Importance { get; init; }
    public Dictionary<string, string> Metadata { get; init; } = [];
    public DateTimeOffset CreatedAt { get; init; }
}

public enum MemoryKind
{
    Episodic = 0,
    Semantic = 1,
    Procedural = 2,
    Clinical = 3,
}
