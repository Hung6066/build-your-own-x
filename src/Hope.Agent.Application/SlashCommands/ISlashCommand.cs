namespace Hope.Agent.Application.SlashCommands;

/// <summary>
/// Context passed to slash-command handlers. <see cref="UserId"/> is the deterministic
/// agent user id used by the runtime. <see cref="RawText"/> is the original message text
/// (still starting with '/').
/// </summary>
public sealed record SlashCommandContext(
    Guid UserId,
    Guid? ConversationId,
    string Command,
    string Arguments,
    string RawText,
    string Channel);

/// <summary>
/// Result of dispatching a slash command. When <see cref="Handled"/> is true, the inbound
/// channel adapter must short-circuit and send <see cref="Reply"/> directly to the user
/// without invoking <c>IAgentRuntime</c>.
/// </summary>
public sealed record SlashCommandResult(bool Handled, string Reply)
{
    public static SlashCommandResult NotHandled { get; } = new(false, string.Empty);
    public static SlashCommandResult Ok(string reply) => new(true, reply);
}

public interface ISlashCommandHandler
{
    /// <summary>Command name without the leading slash, lower-case (e.g. "personality").</summary>
    string Name { get; }
    string Description { get; }
    Task<SlashCommandResult> ExecuteAsync(SlashCommandContext ctx, CancellationToken ct);
}

public interface ISlashCommandRouter
{
    /// <summary>
    /// Returns <see cref="SlashCommandResult.NotHandled"/> if <paramref name="text"/> is not a slash command
    /// or the command is unknown. Otherwise dispatches to the matching handler and returns its result.
    /// </summary>
    Task<SlashCommandResult> TryHandleAsync(Guid userId, Guid? conversationId, string channel, string text, CancellationToken ct);

    IReadOnlyList<ISlashCommandHandler> Handlers { get; }
}
