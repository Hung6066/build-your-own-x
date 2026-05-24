namespace Hope.Agent.Infrastructure.Channels.Zalo;

public sealed class ZaloOptions
{
    public const string Section = "Channels:Zalo";

    public bool Enabled { get; set; }
    public string AppSecret { get; set; } = string.Empty;
    public string OaAccessToken { get; set; } = string.Empty;
    public string ApiBaseUrl { get; set; } = "https://openapi.zalo.me";
    public string[] AllowedSenderIds { get; set; } = [];
    public string AgentProfile { get; set; } = "clinical-mobile";
    public int MaxReplyLength { get; set; } = 2000;
}
