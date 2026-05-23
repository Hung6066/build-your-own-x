using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Hope.Agent.Tools.Mcp;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Hope.Agent.Api.Security;

public sealed class ApiKeyAuthOptions : AuthenticationSchemeOptions;

/// <summary>
/// Validates the X-Api-Key header against SHA-256 hashes stored in McpOptions.ApiKeyHashes.
/// Keys are compared in constant time to prevent timing attacks.
/// </summary>
public sealed class ApiKeyAuthHandler(
    IOptionsMonitor<ApiKeyAuthOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IOptions<McpOptions> mcpOpts) : AuthenticationHandler<ApiKeyAuthOptions>(options, logger, encoder)
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
        var hashes = mcpOpts.Value.ApiKeyHashes;

        if (hashes is not { Count: > 0 })
            return Task.FromResult(AuthenticateResult.Fail("No API keys configured."));

        // Constant-time comparison across all configured hashes
        var matched = hashes.Any(h =>
            CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(inputHash),
                Encoding.UTF8.GetBytes(h)));

        if (!matched)
            return Task.FromResult(AuthenticateResult.Fail("Invalid API key."));

        var claims = new Claim[]
        {
            new(ClaimTypes.Name, "mcp-api-key-client"),
            new(ClaimTypes.AuthenticationMethod, SchemeName),
            new("scope", "hope-agent:mcp"),
        };
        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(ticket));
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
}
