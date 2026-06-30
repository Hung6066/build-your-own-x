using Hope.Agent.Domain.Security;

namespace Hope.Agent.Application.Security;

public sealed record ApiKeyCreateResult(Guid Id, string Name, string RawKey, string Hash, DateTimeOffset? ExpiresAt);

public interface IApiKeyLifecycleStore
{
    Task<ApiKeyRecord?> FindValidAsync(string hash, CancellationToken ct);
    Task<IReadOnlyList<ApiKeyRecord>> ListAsync(Guid tenantId, int take, CancellationToken ct);
    Task<ApiKeyCreateResult> CreateAsync(Guid tenantId, string name, string scope, DateTimeOffset? expiresAt, string? createdBy, CancellationToken ct);
    Task<bool> RevokeAsync(Guid id, string? reason, string? revokedBy, CancellationToken ct);
    Task<ApiKeyCreateResult?> RotateAsync(Guid id, DateTimeOffset? expiresAt, string? rotatedBy, CancellationToken ct);
}
