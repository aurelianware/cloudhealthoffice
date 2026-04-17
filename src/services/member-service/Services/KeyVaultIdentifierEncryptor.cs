using System.Security.Cryptography;
using System.Text;
using CloudHealthOffice.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;

namespace MemberService.Services;

/// <summary>
/// AES-256-GCM identifier encryptor. The data encryption key is fetched from the
/// configured secret provider (Azure Key Vault in prod) — no key material is
/// maintained in this process. Reuses the existing <see cref="ISecretProvider"/>
/// pattern rather than adding another crypto layer.
///
/// Ciphertext format (base64url of):
///   [1 byte version = 0x01][12 bytes IV][16 bytes tag][ciphertext bytes]
/// </summary>
public sealed class KeyVaultIdentifierEncryptor : IIdentifierEncryptor
{
    private const byte Version = 0x01;
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly ISecretProvider _secrets;
    private readonly ILogger<KeyVaultIdentifierEncryptor> _logger;
    private readonly string _keySecretName;

    private byte[]? _cachedKey;
    private readonly SemaphoreSlim _keyLock = new(1, 1);

    public KeyVaultIdentifierEncryptor(
        ISecretProvider secrets,
        ILogger<KeyVaultIdentifierEncryptor> logger,
        string keySecretName)
    {
        _secrets = secrets;
        _logger = logger;
        _keySecretName = string.IsNullOrWhiteSpace(keySecretName)
            ? throw new ArgumentException("keySecretName is required", nameof(keySecretName))
            : keySecretName;
    }

    public bool IsEnabled => true;

    public async Task<string?> EncryptAsync(string? plaintext, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(plaintext)) return plaintext;

        var key = await GetKeyAsync(ct);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var cipherBytes = new byte[plainBytes.Length];
        var tag = new byte[TagSize];

        using var gcm = new AesGcm(key, TagSize);
        gcm.Encrypt(nonce, plainBytes, cipherBytes, tag);

        var envelope = new byte[1 + NonceSize + TagSize + cipherBytes.Length];
        envelope[0] = Version;
        Buffer.BlockCopy(nonce, 0, envelope, 1, NonceSize);
        Buffer.BlockCopy(tag, 0, envelope, 1 + NonceSize, TagSize);
        Buffer.BlockCopy(cipherBytes, 0, envelope, 1 + NonceSize + TagSize, cipherBytes.Length);

        return Base64UrlEncode(envelope);
    }

    public async Task<string?> DecryptAsync(string? ciphertext, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(ciphertext)) return ciphertext;

        byte[] envelope;
        try
        {
            envelope = Base64UrlDecode(ciphertext);
        }
        catch (FormatException)
        {
            _logger.LogWarning("Identifier ciphertext is not valid base64url");
            throw;
        }

        if (envelope.Length < 1 + NonceSize + TagSize)
            throw new CryptographicException("Envelope too short");
        if (envelope[0] != Version)
            throw new CryptographicException($"Unsupported envelope version 0x{envelope[0]:X2}");

        var nonce = new byte[NonceSize];
        var tag = new byte[TagSize];
        var cipherLen = envelope.Length - 1 - NonceSize - TagSize;
        var cipherBytes = new byte[cipherLen];
        Buffer.BlockCopy(envelope, 1, nonce, 0, NonceSize);
        Buffer.BlockCopy(envelope, 1 + NonceSize, tag, 0, TagSize);
        Buffer.BlockCopy(envelope, 1 + NonceSize + TagSize, cipherBytes, 0, cipherLen);

        var key = await GetKeyAsync(ct);
        var plainBytes = new byte[cipherLen];
        using var gcm = new AesGcm(key, TagSize);
        gcm.Decrypt(nonce, cipherBytes, tag, plainBytes);
        return Encoding.UTF8.GetString(plainBytes);
    }

    private async Task<byte[]> GetKeyAsync(CancellationToken ct)
    {
        if (_cachedKey != null) return _cachedKey;

        await _keyLock.WaitAsync(ct);
        try
        {
            if (_cachedKey != null) return _cachedKey;

            var raw = await _secrets.GetSecretAsync(_keySecretName, ct)
                ?? throw new InvalidOperationException(
                    $"Identifier encryption key '{_keySecretName}' not found in secret provider. " +
                    "Provision a 32-byte key in Key Vault under this name.");

            byte[] keyBytes;
            try
            {
                keyBytes = Convert.FromBase64String(raw);
            }
            catch (FormatException)
            {
                keyBytes = Encoding.UTF8.GetBytes(raw);
            }

            if (keyBytes.Length != 32)
                throw new InvalidOperationException(
                    $"Identifier encryption key must be 32 bytes (AES-256); got {keyBytes.Length}.");

            _cachedKey = keyBytes;
            return keyBytes;
        }
        finally
        {
            _keyLock.Release();
        }
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string s)
    {
        var padded = s.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2: padded += "=="; break;
            case 3: padded += "="; break;
        }
        return Convert.FromBase64String(padded);
    }

}
