using Hope.Agent.Domain.Personalization;

namespace Hope.Agent.Application.Personalization;

public interface IUserPreferenceStore
{
    Task<UserPreference?> GetAsync(Guid userId, CancellationToken ct);
    Task SetAgentProfileAsync(Guid userId, string? profile, CancellationToken ct);
    Task SetModelAsync(Guid userId, string? provider, string? model, CancellationToken ct);
    Task SetLocaleAsync(Guid userId, string? language, string? channel, CancellationToken ct);
    Task SetSafetyAsync(Guid userId, string? persona, string? safetyMode, string? purpose, CancellationToken ct);
    Task SetMetadataAsync(Guid userId, string preferencesJson, string? updatedBy, CancellationToken ct);
}
