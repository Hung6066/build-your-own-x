using Hope.Agent.Application.Personalization;
using Hope.Agent.Domain.Personalization;
using Hope.Agent.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Hope.Agent.Infrastructure.Personalization;

internal sealed class EfUserPreferenceStore(AgentDbContext db) : IUserPreferenceStore
{
    public async Task<UserPreference?> GetAsync(Guid userId, CancellationToken ct) =>
        await db.UserPreferences.AsNoTracking().FirstOrDefaultAsync(p => p.UserId == userId, ct);

    public Task SetAgentProfileAsync(Guid userId, string? profile, CancellationToken ct) =>
        UpsertAsync(userId, p => p.AgentProfile = profile, ct);

    public Task SetModelAsync(Guid userId, string? provider, string? model, CancellationToken ct) =>
        UpsertAsync(userId, p =>
        {
            p.PreferredProvider = provider;
            p.PreferredModel = model;
        }, ct);

    public Task SetLocaleAsync(Guid userId, string? language, string? channel, CancellationToken ct) =>
        UpsertAsync(userId, p =>
        {
            p.PreferredLanguage = language;
            p.PreferredChannel = channel;
        }, ct);

    public Task SetSafetyAsync(Guid userId, string? persona, string? safetyMode, string? purpose, CancellationToken ct) =>
        UpsertAsync(userId, p =>
        {
            p.Persona = persona;
            p.SafetyMode = safetyMode;
            p.Purpose = purpose;
        }, ct);

    public Task SetMetadataAsync(Guid userId, string preferencesJson, string? updatedBy, CancellationToken ct) =>
        UpsertAsync(userId, p =>
        {
            p.PreferencesJson = string.IsNullOrWhiteSpace(preferencesJson) ? "{}" : preferencesJson;
            p.UpdatedBy = updatedBy;
            p.Version = $"pref-{Guid.CreateVersion7():N}";
        }, ct);

    private async Task UpsertAsync(Guid userId, Action<UserPreference> mutate, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var existing = await db.UserPreferences.FirstOrDefaultAsync(p => p.UserId == userId, ct);
        if (existing is null)
        {
            var fresh = new UserPreference
            {
                UserId = userId,
                TenantId = Hope.Agent.Application.Security.SecurityDefaults.DefaultTenantId,
                SafetyMode = "clinical-safe",
                Purpose = "treatment",
                PreferencesJson = "{}",
                Version = $"pref-{Guid.CreateVersion7():N}",
                UpdatedAt = now,
            };
            mutate(fresh);
            await db.UserPreferences.AddAsync(fresh, ct);
        }
        else
        {
            mutate(existing);
            existing.UpdatedAt = now;
        }
        await db.SaveChangesAsync(ct);
    }
}
