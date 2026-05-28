using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hope.Agent.Application.Security;
using StackExchange.Redis;

namespace Hope.Agent.Infrastructure.Security;

/// <summary>
/// RFC 9449 DPoP validator backed by Redis for jti replay detection.
/// Supports RSA and EC P-256 client keys.
/// </summary>
internal sealed class DpopValidator(IConnectionMultiplexer redis) : IDpopValidator
{
    private const int MaxClockSkewSeconds = 60;
    private static readonly TimeSpan JtiTtl = TimeSpan.FromMinutes(5);

    public async Task<DpopValidationResult> ValidateAsync(
        string proof, string httpMethod, string requestUri, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(proof))
            return new(false, null, "missing_proof");

        var parts = proof.Split('.');
        if (parts.Length != 3)
            return new(false, null, "malformed");

        JsonElement header, payload;
        try
        {
            header = JsonDocument.Parse(B64UrlDecode(parts[0])).RootElement;
            payload = JsonDocument.Parse(B64UrlDecode(parts[1])).RootElement;
        }
        catch
        {
            return new(false, null, "decode_failed");
        }

        // typ MUST be "dpop+jwt"
        if (!header.TryGetProperty("typ", out var typ) ||
            !string.Equals(typ.GetString(), "dpop+jwt", StringComparison.Ordinal))
            return new(false, null, "bad_typ");

        if (!header.TryGetProperty("jwk", out var jwk))
            return new(false, null, "missing_jwk");

        // htm / htu
        if (!payload.TryGetProperty("htm", out var htm) ||
            !string.Equals(htm.GetString(), httpMethod, StringComparison.OrdinalIgnoreCase))
            return new(false, null, "htm_mismatch");

        if (!payload.TryGetProperty("htu", out var htu) ||
            !UriMatch(htu.GetString(), requestUri))
            return new(false, null, "htu_mismatch");

        // iat skew
        if (!payload.TryGetProperty("iat", out var iat) || iat.ValueKind != JsonValueKind.Number)
            return new(false, null, "missing_iat");
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (Math.Abs(now - iat.GetInt64()) > MaxClockSkewSeconds)
            return new(false, null, "iat_skew");

        // jti replay
        if (!payload.TryGetProperty("jti", out var jti) || jti.ValueKind != JsonValueKind.String)
            return new(false, null, "missing_jti");
        var jtiKey = $"dpop:jti:{jti.GetString()}";
        var db = redis.GetDatabase();
        var ok = await db.StringSetAsync(jtiKey, "1", JtiTtl, when: When.NotExists);
        if (!ok) return new(false, null, "replay");

        // Signature
        if (!VerifySignature(parts, header, jwk, out var thumbprint))
            return new(false, null, "bad_signature");

        return new(true, thumbprint, null);
    }

    private static bool VerifySignature(string[] parts, JsonElement header, JsonElement jwk, out string? thumbprint)
    {
        thumbprint = null;
        var signedInput = Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]);
        var signature = B64UrlDecodeBytes(parts[2]);
        var alg = header.TryGetProperty("alg", out var a) ? a.GetString() : null;
        var kty = jwk.TryGetProperty("kty", out var k) ? k.GetString() : null;

        try
        {
            if (kty == "RSA" && alg == "RS256")
            {
                using var rsa = RSA.Create();
                rsa.ImportParameters(new RSAParameters
                {
                    Modulus = B64UrlDecodeBytes(jwk.GetProperty("n").GetString()!),
                    Exponent = B64UrlDecodeBytes(jwk.GetProperty("e").GetString()!),
                });
                if (!rsa.VerifyData(signedInput, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                    return false;
                thumbprint = RsaThumbprint(jwk);
                return true;
            }
            if (kty == "EC" && alg == "ES256")
            {
                using var ecdsa = ECDsa.Create(new ECParameters
                {
                    Curve = ECCurve.NamedCurves.nistP256,
                    Q = new ECPoint
                    {
                        X = B64UrlDecodeBytes(jwk.GetProperty("x").GetString()!),
                        Y = B64UrlDecodeBytes(jwk.GetProperty("y").GetString()!),
                    },
                });
                if (!ecdsa.VerifyData(signedInput, signature, HashAlgorithmName.SHA256))
                    return false;
                thumbprint = EcThumbprint(jwk);
                return true;
            }
        }
        catch
        {
            return false;
        }
        return false;
    }

    private static bool UriMatch(string? htu, string requestUri)
    {
        if (string.IsNullOrWhiteSpace(htu)) return false;
        // RFC 9449 § 4.2 — compare without query/fragment, case-insensitive for scheme/host.
        if (!Uri.TryCreate(htu, UriKind.Absolute, out var a) ||
            !Uri.TryCreate(requestUri, UriKind.Absolute, out var b))
            return false;
        return string.Equals(a.Scheme, b.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase)
            && a.Port == b.Port
            && string.Equals(a.AbsolutePath.TrimEnd('/'), b.AbsolutePath.TrimEnd('/'), StringComparison.Ordinal);
    }

    // RFC 7638 canonical JWK thumbprint.
    private static string RsaThumbprint(JsonElement jwk)
    {
        var canonical = $"{{\"e\":\"{jwk.GetProperty("e").GetString()}\",\"kty\":\"RSA\",\"n\":\"{jwk.GetProperty("n").GetString()}\"}}";
        return B64UrlEncode(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static string EcThumbprint(JsonElement jwk)
    {
        var crv = jwk.TryGetProperty("crv", out var c) ? c.GetString() : "P-256";
        var canonical = $"{{\"crv\":\"{crv}\",\"kty\":\"EC\",\"x\":\"{jwk.GetProperty("x").GetString()}\",\"y\":\"{jwk.GetProperty("y").GetString()}\"}}";
        return B64UrlEncode(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static byte[] B64UrlDecodeBytes(string s)
    {
        var pad = (s.Length % 4) switch { 2 => "==", 3 => "=", _ => "" };
        return Convert.FromBase64String(s.Replace('-', '+').Replace('_', '/') + pad);
    }

    private static string B64UrlDecode(string s) => Encoding.UTF8.GetString(B64UrlDecodeBytes(s));

    private static string B64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
