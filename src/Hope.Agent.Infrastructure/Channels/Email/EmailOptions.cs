namespace Hope.Agent.Infrastructure.Channels.Email;

public sealed class EmailOptions
{
    public const string Section = "Channels:Email";

    public bool Enabled { get; set; }
    public string SmtpHost { get; set; } = string.Empty;
    public int SmtpPort { get; set; } = 587;
    public bool UseStartTls { get; set; } = true;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string FromAddress { get; set; } = string.Empty;
    public string FromDisplayName { get; set; } = "Hope Agent";
    public string DefaultSubject { get; set; } = "Hope Agent notification";
    public int TimeoutSeconds { get; set; } = 15;
}
