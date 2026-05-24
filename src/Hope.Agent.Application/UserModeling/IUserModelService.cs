using Hope.Agent.Domain.UserModeling;

namespace Hope.Agent.Application.UserModeling;

public sealed record UserTraitsSnapshot(
    string? Role,
    string? Specialty,
    string? CommunicationStyle,
    string? PreferredLanguage)
{
    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(Role) &&
        string.IsNullOrWhiteSpace(Specialty) &&
        string.IsNullOrWhiteSpace(CommunicationStyle) &&
        string.IsNullOrWhiteSpace(PreferredLanguage);

    /// <summary>Render as a system-prompt fragment, or empty string if no traits known.</summary>
    public string ToSystemPromptFragment()
    {
        if (IsEmpty) return string.Empty;
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(Role)) parts.Add($"role={Role}");
        if (!string.IsNullOrWhiteSpace(Specialty)) parts.Add($"specialty={Specialty}");
        if (!string.IsNullOrWhiteSpace(CommunicationStyle)) parts.Add($"communication_style={CommunicationStyle}");
        if (!string.IsNullOrWhiteSpace(PreferredLanguage)) parts.Add($"language={PreferredLanguage}");
        return "Known clinician traits: " + string.Join("; ", parts) + ". Adapt tone and terminology accordingly.";
    }
}

public interface IUserModelService
{
    /// <summary>Return the cached traits for the given user, or null if none yet.</summary>
    Task<UserTraitsSnapshot?> GetAsync(Guid userId, CancellationToken ct);

    /// <summary>
    /// Refresh traits for the given user from the last N conversation turns when the
    /// turn-count delta since last extraction exceeds the configured interval. Idempotent.
    /// </summary>
    Task TryExtractAsync(Guid userId, Guid conversationId, CancellationToken ct);
}

public sealed class UserModelOptions
{
    public const string Section = "UserModel";
    public bool Enabled { get; set; }
    /// <summary>Extract every N user turns.</summary>
    public int ExtractEveryTurns { get; set; } = 10;
    public int RecentTurnsWindow { get; set; } = 30;
}
