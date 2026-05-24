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

    private async Task UpsertAsync(Guid userId, Action<UserPreference> mutate, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var existing = await db.UserPreferences.FirstOrDefaultAsync(p => p.UserId == userId, ct);
        if (existing is null)
        {
            var fresh = new UserPreference { UserId = userId, UpdatedAt = now };
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
