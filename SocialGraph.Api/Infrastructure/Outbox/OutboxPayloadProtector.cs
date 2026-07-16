namespace SocialGraph.Api.Infrastructure.Outbox;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

public interface IOutboxPayloadProtector
{
    string Protect(string plaintext);
    string Unprotect(string protectedPayload);
}

public sealed class OutboxPayloadProtector : IOutboxPayloadProtector
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private readonly byte[] _key;

    public OutboxPayloadProtector(IConfiguration configuration)
    {
        var secret = new[]
            {
                configuration["IntegrationOutbox:PayloadEncryptionKey"],
                configuration["InternalServices:SocialGraph:SharedSecret"],
                configuration["Gateway:InternalSharedSecret"]
            }
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ??
            string.Empty;
        if (Encoding.UTF8.GetByteCount(secret) < 32)
        {
            throw new InvalidOperationException(
                "IntegrationOutbox:PayloadEncryptionKey must contain at least 32 UTF-8 bytes before user provisioning can be queued.");
        }

        _key = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
    }

    public string Protect(string plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var ciphertext = new byte[plaintextBytes.Length];
        var tag = new byte[TagSize];
        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, plaintextBytes, ciphertext, tag);
        return JsonSerializer.Serialize(new ProtectedPayloadEnvelope(
            1,
            Convert.ToBase64String(nonce),
            Convert.ToBase64String(ciphertext),
            Convert.ToBase64String(tag)));
    }

    public string Unprotect(string protectedPayload)
    {
        try
        {
            var envelope = JsonSerializer.Deserialize<ProtectedPayloadEnvelope>(protectedPayload)
                ?? throw new PermanentOutboxException("Protected outbox payload is empty.");
            if (envelope.Version != 1)
            {
                throw new PermanentOutboxException($"Unsupported protected outbox payload version {envelope.Version}.");
            }

            var nonce = Convert.FromBase64String(envelope.Nonce);
            var ciphertext = Convert.FromBase64String(envelope.Ciphertext);
            var tag = Convert.FromBase64String(envelope.Tag);
            var plaintext = new byte[ciphertext.Length];
            using var aes = new AesGcm(_key, TagSize);
            aes.Decrypt(nonce, ciphertext, tag, plaintext);
            return Encoding.UTF8.GetString(plaintext);
        }
        catch (PermanentOutboxException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or FormatException or CryptographicException)
        {
            throw new PermanentOutboxException("Protected outbox payload could not be decrypted.", exception);
        }
    }

    private sealed record ProtectedPayloadEnvelope(
        int Version,
        string Nonce,
        string Ciphertext,
        string Tag);
}
