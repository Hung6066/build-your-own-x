namespace Hope.Agent.Application.Security;

/// <summary>
/// Result of a DPoP (RFC 9449) proof validation.
/// </summary>
public sealed record DpopValidationResult(
    bool IsValid,
    string? Thumbprint,
    string? Reason);

/// <summary>
/// Validates an RFC 9449 DPoP proof header. The proof is a short-lived JWS the
/// client constructs per request, signed by the same key whose thumbprint is
/// bound to the access token in the <c>cnf.jkt</c> claim. This converts the
/// bearer token into a sender-constrained token — a stolen token alone cannot
/// be used because the attacker lacks the private key.
/// </summary>
public interface IDpopValidator
{
    /// <summary>
    /// Validates the supplied DPoP proof:
    ///   <list type="bullet">
    ///     <item>JWS signature against the embedded <c>jwk</c> header.</item>
    ///     <item><c>htm</c> claim matches the HTTP method.</item>
    ///     <item><c>htu</c> claim matches the request URI (host + path, scheme/case-insensitive).</item>
    ///     <item><c>iat</c> within +/-60 s.</item>
    ///     <item><c>jti</c> not previously seen (replay cache).</item>
    ///   </list>
    /// On success returns the JWK SHA-256 thumbprint so the caller can compare it
    /// to the access-token <c>cnf.jkt</c> claim.
    /// </summary>
    Task<DpopValidationResult> ValidateAsync(
        string proof,
        string httpMethod,
        string requestUri,
        CancellationToken ct);
}
