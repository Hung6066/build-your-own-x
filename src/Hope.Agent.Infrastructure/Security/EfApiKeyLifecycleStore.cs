using System.Security.Cryptography;
using System.Text;
using Hope.Agent.Application.Security;
using Hope.Agent.Domain.Security;
using Hope.Agent.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Hope.Agent.Infrastructure.Security;

internal sealed class EfApiKeyLifecycleStore(IDbContextFactory<AgentDbContext> dbFactory) : IApiKeyLifecycleStore
{
    public async Task<ApiKeyRecord?> FindValidAsync(string hash, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        return await db.ApiKeyRecords.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Hash == hash && !x.Revoked && (x.ExpiresAt == null || x.ExpiresAt > now), ct)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ApiKeyRecord>> ListAsync(Guid tenantId, int take, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.ApiKeyRecords.AsNoTracking()
            .Where(x => x.TenantId == tenantId)
            .OrderByDescending(x => x.CreatedAt)
            .Take(Math.Clamp(take, 1, 200))
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<ApiKeyCreateResult> CreateAsync(Guid tenantId, string name, string scope, DateTimeOffset? expiresAt, string? createdBy, CancellationToken ct)
    {
        var raw = CreateRawKey();
        var hash = Hash(raw);
        var record = new ApiKeyRecord
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            Name = string.IsNullOrWhiteSpace(name) ? "api-key" : name,
            Hash = hash,
            Scope = string.IsNullOrWhiteSpace(scope) ? "hope-agent:mcp" : scope,
            ExpiresAt = expiresAt,
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedBy = createdBy,
        };
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        db.ApiKeyRecords.Add(record);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return new ApiKeyCreateResult(record.Id, record.Name, raw, hash, record.ExpiresAt);
    }

    public async Task<bool> RevokeAsync(Guid id, string? reason, string? revokedBy, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var updated = await db.ApiKeyRecords
            .Where(x => x.Id == id && !x.Revoked)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Revoked, true)
                .SetProperty(x => x.RevokedAt, DateTimeOffset.UtcNow)
                .SetProperty(x => x.RevokedBy, revokedBy)
                .SetProperty(x => x.Reason, reason), ct)
            .ConfigureAwait(false);
        return updated > 0;
    }

    public async Task<ApiKeyCreateResult?> RotateAsync(Guid id, DateTimeOffset? expiresAt, string? rotatedBy, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var old = await db.ApiKeyRecords.FirstOrDefaultAsync(x => x.Id == id, ct).ConfigureAwait(false);
        if (old is null) return null;
        old.Revoked = true;
        old.RevokedAt = DateTimeOffset.UtcNow;
        old.RotatedAt = DateTimeOffset.UtcNow;
        old.RevokedBy = rotatedBy;
        old.Reason = "rotated";

        var raw = CreateRawKey();
        var fresh = new ApiKeyRecord
        {
            Id = Guid.CreateVersion7(),
            TenantId = old.TenantId,
            Name = old.Name,
            Hash = Hash(raw),
            Scope = old.Scope,
            ExpiresAt = expiresAt ?? old.ExpiresAt,
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedBy = rotatedBy,
        };
        db.ApiKeyRecords.Add(fresh);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return new ApiKeyCreateResult(fresh.Id, fresh.Name, raw, fresh.Hash, fresh.ExpiresAt);
    }

    public static string Hash(string raw)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));

    private static string CreateRawKey()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return "hope_" + Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
