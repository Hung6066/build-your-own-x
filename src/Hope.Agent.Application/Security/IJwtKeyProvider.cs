namespace Hope.Agent.Application.Security;

/// <summary>
/// Signing material returned by <see cref="IJwtKeyProvider"/>.
/// Supports both symmetric (HS256) and asymmetric (RS256) operation.
/// <list type="bullet">
///   <item>HS256 mode — <see cref="CurrentSecret"/> + optional <see cref="PreviousSecret"/>.</item>
///   <item>RS256 mode — <see cref="CurrentPrivateKeyPem"/> + <see cref="CurrentPublicKeyPem"/>
///         (and optional <c>Previous*</c> for rotation). Public key(s) are exposed via
///         <c>/.well-known/jwks.json</c>; the private key never leaves the auth process.</item>
/// </list>
/// </summary>
public sealed record JwtSigningKeySet(
    string CurrentSecret,
    string? PreviousSecret,
    string? KeyId,
    string Algorithm = "HS256",
    string? CurrentPrivateKeyPem = null,
    string? CurrentPublicKeyPem = null,
    string? PreviousPublicKeyPem = null,
    string? PreviousKeyId = null);

public interface IJwtKeyProvider
{
    JwtSigningKeySet GetSigningKeys();
}