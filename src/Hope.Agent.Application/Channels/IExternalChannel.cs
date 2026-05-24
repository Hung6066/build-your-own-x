namespace Hope.Agent.Application.Channels;

/// <summary>
/// Outbound external messaging channel (Telegram, Zalo, Slack, Email, …).
/// Implementations are stateless and safe to share as singletons.
/// </summary>
public interface IExternalChannel
{
    /// <summary>Logical channel name, e.g. "zalo", "slack", "email". Case-insensitive.</summary>
    string Name { get; }

    /// <summary>
    /// Deliver a plain-text message to the given channel-specific recipient
    /// (Zalo user_id, Slack channel/user id, email address, …).
    /// </summary>
    Task SendAsync(string recipientId, string text, CancellationToken ct);
}

public interface IChannelRegistry
{
    IExternalChannel? Find(string name);
    IReadOnlyList<IExternalChannel> All { get; }
}

/// <summary>
/// A normalized inbound message from any external channel, used by <see cref="IChannelMessageRouter"/>.
/// </summary>
public sealed record InboundChannelMessage(
    string Channel,
    string ExternalUserId,
    string ExternalChatId,
    string Text,
    string? AgentProfile = null,
    string? CorrelationId = null);

/// <summary>
/// Routes a normalized inbound channel message through the agent runtime and returns the reply text.
/// Maps the external user id deterministically to an internal Guid for session tracking.
/// </summary>
public interface IChannelMessageRouter
{
    Task<string> RouteAsync(InboundChannelMessage msg, CancellationToken ct);
}
