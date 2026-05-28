using System.Security.Cryptography;
using Hope.Agent.Application.Security;
using Microsoft.IdentityModel.Tokens;

namespace Hope.Agent.Api.Endpoints;

/// <summary>
/// Publishes the JSON Web Key Set so downstream services (Gateway, MCP clients,
/// microservices) can verify access tokens with the public key only.
/// HS256 deployments expose an empty set — symmetric keys must never be published.
/// </summary>
public static class JwksEndpoint
{
    public static IEndpointRouteBuilder MapJwks(this IEndpointRouteBuilder app)
    {
        app.MapGet("/.well-known/jwks.json", (IJwtKeyProvider keyProvider) =>
        {
            var set = keyProvider.GetSigningKeys();
            var keys = new List<object>();

            if (string.Equals(set.Algorithm, "RS256", StringComparison.OrdinalIgnoreCase))
            {
                AddRsaKey(keys, set.CurrentPublicKeyPem,
                    string.IsNullOrWhiteSpace(set.KeyId) ? "current" : set.KeyId);
                AddRsaKey(keys, set.PreviousPublicKeyPem,
                    string.IsNullOrWhiteSpace(set.PreviousKeyId) ? "previous" : set.PreviousKeyId);
            }

            return Results.Ok(new { keys });
        })
        .AllowAnonymous()
        .WithTags("Auth");

        return app;
    }

    private static void AddRsaKey(List<object> keys, string? pem, string kid)
    {
        if (string.IsNullOrWhiteSpace(pem)) return;
        using var rsa = RSA.Create();
        rsa.ImportFromPem(pem);
        var parameters = rsa.ExportParameters(includePrivateParameters: false);
        keys.Add(new
        {
            kty = "RSA",
            use = "sig",
            alg = "RS256",
            kid,
            n = Base64UrlEncoder.Encode(parameters.Modulus!),
            e = Base64UrlEncoder.Encode(parameters.Exponent!),
        });
    }
}
