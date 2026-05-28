namespace Hope.Agent.Application.Security;

/// <summary>Claims decoded from a consumed refresh token.</summary>
public sealed record RefreshTokenClaims(
    Guid UserId,
    string Subject,
    string[] Roles,
    Guid FamilyId);

/// <summary>
/// Server-side opaque refresh token store.
/// Implementations MUST guarantee single-use semantics:
/// <see cref="ValidateAndConsumeAsync"/> deletes the token atomically so a stolen
/// token that has already been used cannot be replayed.
/// <para>
/// Implementations also track <em>token families</em> (Auth0/Stripe pattern):
/// when a token from a known family is replayed, the entire family is revoked
/// because the legitimate user's client is presumed compromised.
/// </para>
/// </summary>
public interface IRefreshTokenStore
{
    /// <summary>
    /// Issues the first token of a new family (called from <c>/login</c>).
    /// </summary>
    Task<string> CreateAsync(Guid userId, string subject, string[] roles, CancellationToken ct);

    /// <summary>
    /// Issues the next token of an existing family (called from <c>/refresh</c>
    /// after the previous token has been consumed).
    /// </summary>
    Task<string> CreateInFamilyAsync(Guid userId, string subject, string[] roles, Guid familyId, CancellationToken ct);

    /// <summary>
    /// Validates the token and atomically deletes it (single-use).
    /// Returns <see langword="null"/> if the token is unknown, expired, or already consumed.
    /// </summary>
    Task<RefreshTokenClaims?> ValidateAndConsumeAsync(string token, CancellationToken ct);

    /// <summary>
    /// If the supplied token was previously consumed (the &quot;burned&quot; marker is
    /// still present), returns the family information so callers can revoke the
    /// whole family on replay detection. Returns <see langword="null"/> if the
    /// token was never seen.
    /// </summary>
    Task<RefreshTokenClaims?> LookupBurnedAsync(string token, CancellationToken ct);

    /// <summary>Revokes every active token in the given family. Idempotent.</summary>
    Task RevokeFamilyAsync(Guid userId, Guid familyId, CancellationToken ct);

    /// <summary>Immediately invalidates a refresh token (client-initiated logout).</summary>
    Task RevokeAsync(string token, CancellationToken ct);
}
