namespace Hope.Agent.Infrastructure.Channels.Slack;

public sealed class SlackOptions
{
    public const string Section = "Channels:Slack";

    public bool Enabled { get; set; }
    public string SigningSecret { get; set; } = string.Empty;
    public string BotToken { get; set; } = string.Empty;
    public string ApiBaseUrl { get; set; } = "https://slack.com/api";
    public string[] AllowedChannelIds { get; set; } = [];
    public string AgentProfile { get; set; } = "clinical-mobile";
    public int MaxReplyLength { get; set; } = 3000;

    /// <summary>Max accepted clock skew (seconds) for the X-Slack-Request-Timestamp header.</summary>
    public int MaxRequestSkewSeconds { get; set; } = 300;
}
