using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using Hope.Agent.Api.Middleware;
using Hope.Agent.Api.Security;
using Hope.Agent.Application.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Hope.Agent.Api.Endpoints;

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var grp = app.MapGroup("/v1/auth")
            .WithTags("Auth")
            .WithRequestValidation();

        // ── POST /v1/auth/login ───────────────────────────────────────────────
        // Exchange a service-account client credential for a short-lived access token
        // (default 5 min) and an opaque single-use refresh token (default 7 days).
        // Strictly rate-limited (10 req/min per IP) to prevent brute force.
        grp.MapPost("/login", async (
            [FromBody] LoginRequest req,
            [FromServices] IOptions<AuthOptions> opts,
            [FromServices] ITokenService tokens,
            [FromServices] IRefreshTokenStore store,
            [FromServices] ILoggerFactory loggers,
            HttpContext http,
            CancellationToken ct) =>
        {
            var log = loggers.CreateLogger("Hope.Agent.Auth");
            var ip = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";

            var account = ValidateCredential(req.ClientId, req.Secret, opts.Value);
            if (account is null)
            {
                // Log as Warning so SIEM can alert on repeated failures from the same IP.
                // Reason is intentionally generic — never distinguish "unknown client" vs
                // "wrong secret" to prevent user-enumeration.
                log.LogWarning(
                    "auth.login.failed | clientId={ClientId} ip={Ip} reason=invalid_credential",
                    Truncate(req.ClientId, 64), ip);
                return Results.Unauthorized();
            }

            // Derive a deterministic, stable UserId from the ClientId so the same
            // service account always maps to the same Guid (no user table required).
            var userId = DeriveUserId(req.ClientId);
            var refreshToken = await store.CreateAsync(userId, req.ClientId, account.Roles, ct);
            var accessToken = tokens.IssueAccessToken(userId, req.ClientId, account.Roles);

            log.LogInformation(
                "auth.login.success | clientId={ClientId} userId={UserId} ip={Ip}",
                req.ClientId, userId, ip);

            return Results.Ok(new TokenResponse(
                accessToken, refreshToken, tokens.AccessTokenLifetimeSeconds, "Bearer"));
        })
        .AllowAnonymous()
        .RequireRateLimiting("auth-login");

        // ── POST /v1/auth/refresh ─────────────────────────────────────────────
        // Single-use refresh token rotation.  Old token is atomically consumed before
        // the new pair is issued — a replayed token returns 401 immediately.
        grp.MapPost("/refresh", async (
            [FromBody] RefreshRequest req,
            [FromServices] ITokenService tokens,
            [FromServices] IRefreshTokenStore store,
            [FromServices] ILoggerFactory loggers,
            HttpContext http,
            CancellationToken ct) =>
        {
            var log = loggers.CreateLogger("Hope.Agent.Auth");
            var ip = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";

            var claims = await store.ValidateAndConsumeAsync(req.RefreshToken, ct);
            if (claims is null)
            {
                // Token unknown, expired, or already consumed.
                // If a "burned" tombstone exists, the same token was already used once —
                // this is a replay. Revoke the entire family (Auth0/Stripe pattern):
                // both the legitimate client and the attacker lose access, forcing a re-login.
                var burned = await store.LookupBurnedAsync(req.RefreshToken, ct);
                if (burned is not null)
                {
                    await store.RevokeFamilyAsync(burned.UserId, burned.FamilyId, ct);
                    log.LogWarning(
                        "auth.refresh.replay_family_revoked | userId={UserId} familyId={FamilyId} ip={Ip}",
                        burned.UserId, burned.FamilyId, ip);
                }
                else
                {
                    log.LogWarning(
                        "auth.refresh.replay_or_expired | ip={Ip}", ip);
                }
                return Results.Unauthorized();
            }

            // Both tokens are reissued; old refresh token is already gone.
            // The new refresh token stays in the same family so a future replay can
            // be traced back to this lineage.
            var newRefresh = await store.CreateInFamilyAsync(
                claims.UserId, claims.Subject, claims.Roles, claims.FamilyId, ct);
            var accessToken = tokens.IssueAccessToken(claims.UserId, claims.Subject, claims.Roles);

            log.LogDebug(
                "auth.refresh.success | subject={Subject} userId={UserId} ip={Ip}",
                claims.Subject, claims.UserId, ip);

            return Results.Ok(new TokenResponse(
                accessToken, newRefresh, tokens.AccessTokenLifetimeSeconds, "Bearer"));
        })
        .AllowAnonymous()
        .RequireRateLimiting("auth-refresh");

        // ── POST /v1/auth/revoke ──────────────────────────────────────────────
        // Client-initiated logout: invalidates the supplied refresh token immediately.
        // Always returns 204 regardless of whether the token existed (prevents oracle).
        grp.MapPost("/revoke", async (
            [FromBody] RevokeRequest req,
            [FromServices] IRefreshTokenStore store,
            [FromServices] ILoggerFactory loggers,
            HttpContext http,
            CancellationToken ct) =>
        {
            await store.RevokeAsync(req.RefreshToken, ct);

            var log = loggers.CreateLogger("Hope.Agent.Auth");
            log.LogInformation(
                "auth.revoke | ip={Ip}",
                http.Connection.RemoteIpAddress?.ToString() ?? "unknown");

            return Results.NoContent();
        })
        .AllowAnonymous()
        .RequireRateLimiting("auth-refresh");

        return app;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Validates clientId + secret against the configured service accounts using
    /// constant-time comparison to prevent timing-based clientId enumeration.
    /// Always hashes the input even when no matching account exists.
    /// </summary>
    private static ServiceAccountEntry? ValidateCredential(
        string clientId,
        string secret,
        AuthOptions opts)
    {
        var account = opts.ServiceAccounts
            .FirstOrDefault(a => string.Equals(a.ClientId, clientId, StringComparison.Ordinal));

        // Always compute the hash — prevents short-circuit timing attacks.
        var inputHash = Encoding.UTF8.GetBytes(HashSecret(secret));
        var storedHash = Encoding.UTF8.GetBytes(
            account?.SecretHash.ToLowerInvariant() ?? new string('0', 64));

        var matched = CryptographicOperations.FixedTimeEquals(inputHash, storedHash);
        return matched && account is not null ? account : null;
    }

    /// <summary>
    /// Derives a deterministic stable <see cref="Guid"/> from a clientId string
    /// using SHA-256 so a service account always maps to the same UserId without
    /// needing a database row.
    /// </summary>
    private static Guid DeriveUserId(string clientId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("hope.agent.sa:" + clientId));
        var bytes = hash[..16];
        // Stamp as UUID version 4 / variant 1 (RFC 4122) so the value is recognisable
        // as a UUID by downstream tools even though it's deterministic.
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x40);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes);
    }

    private static string HashSecret(string secret) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret))).ToLowerInvariant();

    // Clamp caller-supplied strings before embedding in log messages to prevent
    // oversized structured values that could saturate log storage.
    private static string Truncate(string value, int maxLen) =>
        value.Length <= maxLen ? value : value[..maxLen] + "…";
}

public sealed record LoginRequest(
    [Required, StringLength(128, MinimumLength = 1)] string ClientId,
    [Required, StringLength(256, MinimumLength = 16)] string Secret);

public sealed record RefreshRequest(
    [Required, StringLength(512, MinimumLength = 16)] string RefreshToken);

public sealed record RevokeRequest(
    [Required, StringLength(512, MinimumLength = 16)] string RefreshToken);

public sealed record TokenResponse(
    string AccessToken,
    string RefreshToken,
    int ExpiresIn,
    string TokenType);
