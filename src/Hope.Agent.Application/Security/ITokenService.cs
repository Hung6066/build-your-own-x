namespace Hope.Agent.Application.Security;

/// <summary>Issued token pair returned to the client on login or refresh.</summary>
public sealed record TokenPair(string AccessToken, string RefreshToken, int ExpiresIn, string TokenType);

/// <summary>
/// Issues short-lived signed JWT access tokens.
/// Refresh token creation is delegated to <see cref="IRefreshTokenStore"/>
/// so that opaque token storage stays independent of signing-key concerns.
/// </summary>
public interface ITokenService
{
    /// <summary>Lifetime of issued access tokens in seconds.</summary>
    int AccessTokenLifetimeSeconds { get; }

    /// <summary>
    /// Creates and signs a JWT access token for the given identity.
    /// The token is valid for <see cref="AccessTokenLifetimeSeconds"/> seconds.
    /// </summary>
    string IssueAccessToken(Guid userId, string subject, string[] roles);
}
