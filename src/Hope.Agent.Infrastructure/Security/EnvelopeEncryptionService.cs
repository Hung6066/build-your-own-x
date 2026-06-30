using System.Security.Cryptography;
using System.Text;
using Hope.Agent.Application.Security;
using Microsoft.Extensions.Options;

namespace Hope.Agent.Infrastructure.Security;

internal sealed class EnvelopeEncryptionService(IOptionsMonitor<SecretManagementOptions> options) : IEnvelopeEncryptionService
{
    public Task<EnvelopeEncryptionResult> EncryptAsync(Guid? tenantId, string plaintext, string purpose, CancellationToken ct)
    {
        var opts = options.CurrentValue;
        var dataKey = RandomNumberGenerator.GetBytes(32);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var cipher = new byte[plainBytes.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(dataKey, 16))
            aes.Encrypt(nonce, plainBytes, cipher, tag, BuildAad(tenantId, purpose));

        var envelopeBytes = nonce.Concat(tag).Concat(cipher).ToArray();
        var wrappedKey = ProtectedDataLikeWrap(dataKey, opts.KmsKeyId, tenantId);
        return Task.FromResult(new EnvelopeEncryptionResult(
            Convert.ToBase64String(envelopeBytes),
            Convert.ToBase64String(wrappedKey),
            string.IsNullOrWhiteSpace(opts.KmsKeyId) ? "local-dev-kek" : opts.KmsKeyId,
            "AES-256-GCM+KMS-ENVELOPE",
            tenantId,
            purpose,
            DateTimeOffset.UtcNow));
    }

    public Task<string> DecryptAsync(Guid? tenantId, EnvelopeEncryptionResult envelope, CancellationToken ct)
    {
        var dataKey = ProtectedDataLikeUnwrap(Convert.FromBase64String(envelope.EncryptedDataKeyBase64), envelope.KmsKeyId, tenantId);
        var bytes = Convert.FromBase64String(envelope.CiphertextBase64);
        var nonce = bytes[..12];
        var tag = bytes[12..28];
        var cipher = bytes[28..];
        var plain = new byte[cipher.Length];
        using (var aes = new AesGcm(dataKey, 16))
            aes.Decrypt(nonce, cipher, tag, plain, BuildAad(tenantId, envelope.Purpose));
        return Task.FromResult(Encoding.UTF8.GetString(plain));
    }

    private static byte[] BuildAad(Guid? tenantId, string purpose)
        => Encoding.UTF8.GetBytes($"{tenantId?.ToString() ?? "global"}:{purpose}");

    private static byte[] ProtectedDataLikeWrap(byte[] dataKey, string keyId, Guid? tenantId)
    {
        var kek = SHA256.HashData(Encoding.UTF8.GetBytes($"{keyId}:hope-agent:{tenantId?.ToString() ?? "global"}"));
        return dataKey.Select((b, i) => (byte)(b ^ kek[i % kek.Length])).ToArray();
    }

    private static byte[] ProtectedDataLikeUnwrap(byte[] wrapped, string keyId, Guid? tenantId)
        => ProtectedDataLikeWrap(wrapped, keyId, tenantId);
}
