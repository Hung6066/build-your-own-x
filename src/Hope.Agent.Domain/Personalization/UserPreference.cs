namespace Hope.Agent.Domain.Personalization;

/// <summary>
/// Per-user runtime preferences set via slash commands (e.g. /personality, /model).
/// </summary>
public sealed class UserPreference
{
    public Guid UserId { get; init; }
    public string? AgentProfile { get; set; }
    public string? PreferredProvider { get; set; }
    public string? PreferredModel { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
