using Hope.Agent.Domain.Personalization;

namespace Hope.Agent.Application.Personalization;

public interface IUserPreferenceStore
{
    Task<UserPreference?> GetAsync(Guid userId, CancellationToken ct);
    Task SetAgentProfileAsync(Guid userId, string? profile, CancellationToken ct);
    Task SetModelAsync(Guid userId, string? provider, string? model, CancellationToken ct);
}
