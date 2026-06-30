namespace Hope.Agent.Domain.Personalization;

/// <summary>
/// Per-user runtime preferences set via slash commands (e.g. /personality, /model).
/// </summary>
public sealed class UserPreference
{
    public Guid UserId { get; init; }
    public Guid TenantId { get; set; }
    public string? AgentProfile { get; set; }
    public string? PreferredProvider { get; set; }
    public string? PreferredModel { get; set; }
    public string? PreferredLanguage { get; set; }
    public string? PreferredChannel { get; set; }
    public string? Persona { get; set; }
    public string? SafetyMode { get; set; }
    public string? Purpose { get; set; }
    public string? PreferencesJson { get; set; }
    public string? Version { get; set; }
    public string? UpdatedBy { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
