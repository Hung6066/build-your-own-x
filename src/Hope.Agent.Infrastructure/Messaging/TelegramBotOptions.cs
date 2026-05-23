namespace Hope.Agent.Infrastructure.Messaging;

public sealed class TelegramBotOptions
{
    public const string Section = "Telegram";
    public bool Enabled { get; init; }
    public string BotToken { get; init; } = "";
    /// <summary>Whitelist of Telegram chat IDs allowed to interact with the bot. Empty = deny all.</summary>
    public long[] AllowedChatIds { get; init; } = [];
    /// <summary>Agent profile used for Telegram interactions (e.g. "clinical-mobile").</summary>
    public string AgentProfile { get; init; } = "clinical-mobile";
    /// <summary>Max characters in a single reply. Telegram hard limit is 4096.</summary>
    public int MaxReplyLength { get; init; } = 3000;
}
