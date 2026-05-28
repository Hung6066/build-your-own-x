using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Hope.Agent.Application.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;

namespace Hope.Agent.Api.Security;

/// <summary>
/// Issues short-lived (5-minute default) JWT access tokens. Algorithm is selected
/// by the <see cref="IJwtKeyProvider"/>: HS256 in development, RS256 in production
/// (recommended — verify-only public key is shared via <c>/.well-known/jwks.json</c>
/// so downstream services cannot forge tokens).
/// </summary>
internal sealed class JwtTokenService(IJwtKeyProvider keyProvider, IConfiguration cfg) : ITokenService
{
    private static readonly JwtSecurityTokenHandler Handler = new() { MapInboundClaims = false };

    public int AccessTokenLifetimeSeconds =>
        cfg.GetValue<int>("Auth:AccessTokenLifetimeMinutes", 5) * 60;

    public string IssueAccessToken(Guid userId, string subject, string[] roles)
    {
        var keys = keyProvider.GetSigningKeys();
        var keyId = string.IsNullOrWhiteSpace(keys.KeyId) ? "current" : keys.KeyId;

        SigningCredentials creds;
        if (string.Equals(keys.Algorithm, "RS256", StringComparison.OrdinalIgnoreCase))
        {
            var rsa = RSA.Create();
            rsa.ImportFromPem(keys.CurrentPrivateKeyPem);
            var rsaKey = new RsaSecurityKey(rsa) { KeyId = keyId };
            creds = new SigningCredentials(rsaKey, SecurityAlgorithms.RsaSha256);
        }
        else
        {
            var symKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(keys.CurrentSecret))
            {
                KeyId = keyId,
            };
            creds = new SigningCredentials(symKey, SecurityAlgorithms.HmacSha256);
        }

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, subject),
            new(JwtRegisteredClaimNames.Jti, Guid.CreateVersion7().ToString()),
            new(ClaimTypes.NameIdentifier, userId.ToString()),
        };
        foreach (var role in roles)
            claims.Add(new Claim(ClaimTypes.Role, role));

        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            Issuer = cfg["Jwt:Issuer"],
            Audience = cfg["Jwt:Audience"],
            Expires = DateTime.UtcNow.AddSeconds(AccessTokenLifetimeSeconds),
            SigningCredentials = creds,
        };

        return Handler.WriteToken(Handler.CreateToken(descriptor));
    }
}
