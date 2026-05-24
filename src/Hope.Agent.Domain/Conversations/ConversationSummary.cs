namespace Hope.Agent.Domain.Conversations;

/// <summary>
/// Compressed summary of older messages within a single conversation, produced by
/// <c>IConversationCompressor</c> when the context window pressure exceeds threshold.
/// At most one row per conversation; replaced (upserted) on each compression pass.
/// </summary>
public sealed class ConversationSummary
{
    public Guid ConversationId { get; init; }
    public required string Content { get; set; }
    public int SummarizedMessageCount { get; set; }
    public DateTimeOffset SummarizedUpTo { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
