using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Hope.Agent.Tools.Mcp;
using Hope.Agent.Application.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Hope.Agent.Api.Security;

public sealed class ApiKeyAuthOptions : AuthenticationSchemeOptions;

/// <summary>
/// Validates the X-Api-Key header. Supports two sources:
/// 1. McpOptions.ApiKeys — lifecycle-managed entries (name, expiry, revocation),
///    hot-reloaded via IOptionsMonitor so rotation/revocation applies without restart.
/// 2. McpOptions.ApiKeyHashes — legacy flat hash list (no lifecycle).
/// Keys are compared in constant time to prevent timing attacks.
/// </summary>
public sealed class ApiKeyAuthHandler(
    IOptionsMonitor<ApiKeyAuthOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IOptionsMonitor<McpOptions> mcpOpts,
    IApiKeyLifecycleStore keyStore,
    TimeProvider timeProvider) : AuthenticationHandler<ApiKeyAuthOptions>(options, logger, encoder)
{
    public const string SchemeName = "ApiKey";
    private const string HeaderName = "X-Api-Key";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(HeaderName, out var rawKey) || rawKey.Count == 0)
            return Task.FromResult(AuthenticateResult.NoResult());

        var key = rawKey.ToString();
        if (string.IsNullOrWhiteSpace(key))
            return Task.FromResult(AuthenticateResult.Fail("Empty API key."));

        var inputHash = HashKey(key);
        var opts = mcpOpts.CurrentValue;
        var now = timeProvider.GetUtcNow();

        var persisted = keyStore.FindValidAsync(inputHash, Context.RequestAborted).GetAwaiter().GetResult();
        if (persisted is not null)
            return Task.FromResult(Success(persisted.Name, persisted.Scope));

        // Config lifecycle-managed keys: must not be revoked or expired.
        var entry = opts.ApiKeys.FirstOrDefault(e =>
            !string.IsNullOrWhiteSpace(e.Hash)
            && CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(inputHash),
                Encoding.UTF8.GetBytes(e.Hash)));

        string keyName;
        if (entry is not null)
        {
            if (entry.Revoked)
                return Task.FromResult(AuthenticateResult.Fail("API key revoked."));
            if (entry.ExpiresAt is { } exp && exp <= now)
                return Task.FromResult(AuthenticateResult.Fail("API key expired."));
            keyName = string.IsNullOrWhiteSpace(entry.Name) ? "mcp-api-key-client" : entry.Name;
        }
        else
        {
            // Legacy flat hash list (no lifecycle metadata).
            var hashes = opts.ApiKeyHashes;
            if (hashes is not { Count: > 0 } && opts.ApiKeys.Count == 0)
                return Task.FromResult(AuthenticateResult.Fail("No API keys configured."));

            var matched = hashes is { Count: > 0 } && hashes.Any(h =>
                CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(inputHash),
                    Encoding.UTF8.GetBytes(h)));

            if (!matched)
                return Task.FromResult(AuthenticateResult.Fail("Invalid API key."));

            keyName = "mcp-api-key-client";
        }

        return Task.FromResult(Success(keyName, "hope-agent:mcp"));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = $"ApiKey realm=\"Hope.Agent\"";
        return Task.CompletedTask;
    }

    /// <summary>SHA-256 hex of the raw key value.</summary>
    public static string HashKey(string rawKey)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(rawKey));
        return Convert.ToHexStringLower(bytes);
    }

    private AuthenticateResult Success(string keyName, string scope)
    {
        var claims = new Claim[]
        {
            new(ClaimTypes.Name, keyName),
            new(ClaimTypes.AuthenticationMethod, SchemeName),
            new("scope", scope),
        };
        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name);
        return AuthenticateResult.Success(ticket);
    }
}
