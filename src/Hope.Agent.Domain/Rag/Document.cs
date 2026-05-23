namespace Hope.Agent.Domain.Rag;

public sealed class Document
{
    public Guid Id { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Source { get; init; } = "manual";
    public string Collection { get; init; } = "clinical_guidelines";
    public string? Url { get; init; }
    public string ContentHash { get; init; } = string.Empty;
    public DocumentStatus Status { get; set; }
    public int ChunkCount { get; set; }
    public Dictionary<string, string> Metadata { get; init; } = [];
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public enum DocumentStatus
{
    Pending = 0,
    Ingesting = 1,
    Ready = 2,
    Failed = 3,
}

public sealed class DocumentChunk
{
    public Guid Id { get; init; }
    public Guid DocumentId { get; init; }
    public int Ordinal { get; init; }
    public string Content { get; init; } = string.Empty;
    public int TokenEstimate { get; init; }
    public string? SectionPath { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}
