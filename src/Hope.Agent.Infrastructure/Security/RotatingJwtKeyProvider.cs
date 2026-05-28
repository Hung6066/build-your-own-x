using Hope.Agent.Application.Security;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Hope.Agent.Infrastructure.Security;

internal sealed class RotatingJwtKeyProvider(
    IConfiguration cfg,
    IMemoryCache cache,
    ILogger<RotatingJwtKeyProvider> log) : IJwtKeyProvider
{
    private const string CacheKey = "jwt:signing-keys";

    public JwtSigningKeySet GetSigningKeys()
    {
        if (cache.TryGetValue(CacheKey, out JwtSigningKeySet? cached) && cached is not null)
            return cached;

        var algorithm = (cfg["Jwt:Algorithm"] ?? "HS256").ToUpperInvariant();
        var keyId = cfg["Jwt:KeyId"];
        var previousKeyId = cfg["Jwt:PreviousKeyId"];

        JwtSigningKeySet keys;
        if (algorithm == "RS256")
        {
            // RSA private key (PEM) is loaded from a file path so the key material
            // never appears in environment / config dumps. Public counterpart can be
            // supplied directly or loaded from a sibling file.
            var privatePem = LoadPem(cfg["Jwt:PrivateKeyPath"]) ?? cfg["Jwt:PrivateKeyPem"];
            var publicPem = LoadPem(cfg["Jwt:PublicKeyPath"]) ?? cfg["Jwt:PublicKeyPem"];
            var prevPub = LoadPem(cfg["Jwt:PreviousPublicKeyPath"]) ?? cfg["Jwt:PreviousPublicKeyPem"];

            if (string.IsNullOrWhiteSpace(privatePem) || string.IsNullOrWhiteSpace(publicPem))
                throw new InvalidOperationException(
                    "Jwt:Algorithm=RS256 requires Jwt:PrivateKeyPath and Jwt:PublicKeyPath (or *Pem inline).");

            keys = new JwtSigningKeySet(
                CurrentSecret: string.Empty,
                PreviousSecret: null,
                KeyId: keyId,
                Algorithm: "RS256",
                CurrentPrivateKeyPem: privatePem,
                CurrentPublicKeyPem: publicPem,
                PreviousPublicKeyPem: prevPub,
                PreviousKeyId: previousKeyId);
            log.LogInformation("JWT signing configured for RS256 (asymmetric).");
        }
        else
        {
            var current = cfg["Jwt:CurrentSecret"] ?? cfg["Jwt:Secret"] ?? string.Empty;
            var previous = cfg["Jwt:PreviousSecret"];
            keys = new JwtSigningKeySet(
                CurrentSecret: current,
                PreviousSecret: previous,
                KeyId: keyId,
                Algorithm: "HS256",
                PreviousKeyId: previousKeyId);
            log.LogInformation("JWT signing configured for HS256 (symmetric).");
        }

        cache.Set(CacheKey, keys, TimeSpan.FromHours(1));
        return keys;
    }

    private static string? LoadPem(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (!File.Exists(path)) return null;
        return File.ReadAllText(path);
    }
}