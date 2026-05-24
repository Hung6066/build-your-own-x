using Hope.Agent.Domain.Conversations;

namespace Hope.Agent.Application.Compression;

public sealed record CompressionResult(
    ConversationSummary Summary,
    int CompressedMessageCount);

public interface IConversationCompressor
{
    /// <summary>
    /// If the conversation message count exceeds <see cref="ConversationCompressorOptions.TriggerMessageCount"/>,
    /// summarize all but the most recent <c>KeepRecentMessages</c> into one upserted <see cref="ConversationSummary"/>.
    /// Returns null when no compression was needed. Idempotent; safe to call on every turn.
    /// </summary>
    Task<CompressionResult?> MaybeCompressAsync(Domain.Conversations.Conversation conversation, CancellationToken ct);

    /// <summary>Read the stored summary for a conversation, or null if none yet.</summary>
    Task<ConversationSummary?> GetSummaryAsync(Guid conversationId, CancellationToken ct);
}

public sealed class ConversationCompressorOptions
{
    public const string Section = "ConversationCompressor";
    public bool Enabled { get; set; }
    /// <summary>Compress once the conversation has more than this many messages.</summary>
    public int TriggerMessageCount { get; set; } = 40;
    /// <summary>Always keep this many most-recent messages verbatim (not summarized).</summary>
    public int KeepRecentMessages { get; set; } = 12;
}
