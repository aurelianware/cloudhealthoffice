using System.Security.Cryptography;
using System.Text;
using CloudHealthOffice.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;

namespace MemberService.Services;

/// <summary>
/// AES-256-GCM identifier encryptor. The data encryption key is fetched
/// through <see cref="RotatingKeyProvider"/> so rotation is done by
/// publishing a new <c>{prefix}-{version}</c> secret and flipping
/// <c>MemberEncryption:CurrentKeyVersion</c> — no code change.
///
/// Envelope formats (base64url of):
///   0x01: [0x01][12 IV][16 tag][ciphertext]
///          — legacy, decrypt only, key resolved via LegacyKeySecretName.
///   0x02: [0x02][keyVerLen=1 byte][keyVer UTF-8 bytes][12 IV][16 tag][ciphertext]
///          — all new encryptions. keyVer is an operator-controlled
///            version string that must appear in AcceptedKeyVersions for
///            decryption to succeed.
///
/// 0x01 decoding is retained indefinitely. A record written under 0x01
/// stays decryptable until a separate backfill re-encrypts it under 0x02.
/// </summary>
public sealed class KeyVaultIdentifierEncryptor : IIdentifierEncryptor
{
    private const byte FormatV1 = 0x01;
    private const byte FormatV2 = 0x02;
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly RotatingKeyProvider _keys;
    private readonly ISecretProvider _secrets;
    private readonly ILogger<KeyVaultIdentifierEncryptor> _logger;
    private readonly MemberEncryptionOptions _options;

    private byte[]? _cachedLegacyKey;
    private readonly SemaphoreSlim _legacyKeyLock = new(1, 1);

    public KeyVaultIdentifierEncryptor(
        RotatingKeyProvider keys,
        ISecretProvider secrets,
        ILogger<KeyVaultIdentifierEncryptor> logger,
        MemberEncryptionOptions options)
    {
        _keys = keys;
        _secrets = secrets;
        _logger = logger;
        _options = options;

        if (string.IsNullOrWhiteSpace(options.KeySecretPrefix))
            throw new ArgumentException("KeySecretPrefix is required", nameof(options));
        if (string.IsNullOrWhiteSpace(options.CurrentKeyVersion))
            throw new ArgumentException("CurrentKeyVersion is required", nameof(options));
        if (options.AcceptedKeyVersions.Count == 0)
            throw new ArgumentException("AcceptedKeyVersions must contain at least one entry", nameof(options));
    }

    public bool IsEnabled => true;

    public async Task<string?> EncryptAsync(string? plaintext, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(plaintext)) return plaintext;

        return _options.EmitLegacyEnvelope
            ? await EncryptV1Async(plaintext, ct)
            : await EncryptV2Async(plaintext, ct);
    }

    private async Task<string> EncryptV1Async(string plaintext, CancellationToken ct)
    {
        var key = await GetLegacyKeyAsync(ct);
        EnsureKeyLength(key);

        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var cipherBytes = new byte[plainBytes.Length];
        var tag = new byte[TagSize];

        using (var gcm = new AesGcm(key, TagSize))
        {
            gcm.Encrypt(nonce, plainBytes, cipherBytes, tag);
        }

        var envelope = new byte[1 + NonceSize + TagSize + cipherBytes.Length];
        envelope[0] = FormatV1;
        Buffer.BlockCopy(nonce, 0, envelope, 1, NonceSize);
        Buffer.BlockCopy(tag, 0, envelope, 1 + NonceSize, TagSize);
        Buffer.BlockCopy(cipherBytes, 0, envelope, 1 + NonceSize + TagSize, cipherBytes.Length);
        return Base64UrlEncode(envelope);
    }

    private async Task<string> EncryptV2Async(string plaintext, CancellationToken ct)
    {
        var keyVersion = _options.CurrentKeyVersion;
        var keyVersionBytes = Encoding.UTF8.GetBytes(keyVersion);
        if (keyVersionBytes.Length == 0 || keyVersionBytes.Length > 255)
            throw new InvalidOperationException(
                $"MemberEncryption:CurrentKeyVersion '{keyVersion}' must be 1..255 UTF-8 bytes.");

        var key = await ResolveCurrentKeyAsync(keyVersion, ct);
        EnsureKeyLength(key);

        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var cipherBytes = new byte[plainBytes.Length];
        var tag = new byte[TagSize];

        using (var gcm = new AesGcm(key, TagSize))
        {
            gcm.Encrypt(nonce, plainBytes, cipherBytes, tag);
        }

        var envelope = new byte[1 + 1 + keyVersionBytes.Length + NonceSize + TagSize + cipherBytes.Length];
        var o = 0;
        envelope[o++] = FormatV2;
        envelope[o++] = (byte)keyVersionBytes.Length;
        Buffer.BlockCopy(keyVersionBytes, 0, envelope, o, keyVersionBytes.Length); o += keyVersionBytes.Length;
        Buffer.BlockCopy(nonce, 0, envelope, o, NonceSize); o += NonceSize;
        Buffer.BlockCopy(tag, 0, envelope, o, TagSize); o += TagSize;
        Buffer.BlockCopy(cipherBytes, 0, envelope, o, cipherBytes.Length);

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
        catch (FormatException ex)
        {
            // Translate to CryptographicException so the entire decrypt
            // surface has a single failure type for callers — base64
            // decode is an internal implementation detail of the envelope
            // format, not a contract the caller should know about.
            _logger.LogWarning(ex, "Identifier ciphertext is not valid base64url");
            throw new CryptographicException("Identifier ciphertext is not valid base64url", ex);
        }

        if (envelope.Length < 1)
            throw new CryptographicException("Envelope is empty");

        return envelope[0] switch
        {
            FormatV1 => await DecryptV1Async(envelope, ct),
            FormatV2 => await DecryptV2Async(envelope, ct),
            _ => throw new CryptographicException($"Unsupported envelope version 0x{envelope[0]:X2}")
        };
    }

    private async Task<string> DecryptV1Async(byte[] envelope, CancellationToken ct)
    {
        if (envelope.Length < 1 + NonceSize + TagSize)
            throw new CryptographicException("0x01 envelope too short");

        var nonce = new byte[NonceSize];
        var tag = new byte[TagSize];
        var cipherLen = envelope.Length - 1 - NonceSize - TagSize;
        var cipherBytes = new byte[cipherLen];
        Buffer.BlockCopy(envelope, 1, nonce, 0, NonceSize);
        Buffer.BlockCopy(envelope, 1 + NonceSize, tag, 0, TagSize);
        Buffer.BlockCopy(envelope, 1 + NonceSize + TagSize, cipherBytes, 0, cipherLen);

        var key = await GetLegacyKeyAsync(ct);
        EnsureKeyLength(key);

        var plainBytes = new byte[cipherLen];
        using var gcm = new AesGcm(key, TagSize);
        gcm.Decrypt(nonce, cipherBytes, tag, plainBytes);
        return Encoding.UTF8.GetString(plainBytes);
    }

    private async Task<string> DecryptV2Async(byte[] envelope, CancellationToken ct)
    {
        if (envelope.Length < 1 + 1) throw new CryptographicException("0x02 envelope too short");
        int keyVerLen = envelope[1];
        var headerLen = 1 + 1 + keyVerLen;
        if (envelope.Length < headerLen + NonceSize + TagSize)
            throw new CryptographicException("0x02 envelope too short for key-version + IV + tag");

        var keyVersion = Encoding.UTF8.GetString(envelope, 2, keyVerLen);

        if (!_options.AcceptedKeyVersions.Contains(keyVersion, StringComparer.OrdinalIgnoreCase))
            throw new CryptographicException(
                $"Envelope key version '{keyVersion}' is not in MemberEncryption:AcceptedKeyVersions. " +
                "Either widen the accepted window or re-encrypt the record.");

        byte[] key;
        try
        {
            key = await _keys.GetKeyAsync(_options.KeySecretPrefix, keyVersion, devConfigFallback: null, ct);
        }
        catch (InvalidOperationException ex)
        {
            throw new StaleEncryptionKeyException(keyVersion,
                $"Accepted key version '{keyVersion}' cannot be resolved from the secret provider. " +
                "Publish the secret or drop the version from AcceptedKeyVersions once backfill completes.",
                ex);
        }
        EnsureKeyLength(key);

        var nonce = new byte[NonceSize];
        var tag = new byte[TagSize];
        var cipherLen = envelope.Length - headerLen - NonceSize - TagSize;
        var cipherBytes = new byte[cipherLen];
        Buffer.BlockCopy(envelope, headerLen, nonce, 0, NonceSize);
        Buffer.BlockCopy(envelope, headerLen + NonceSize, tag, 0, TagSize);
        Buffer.BlockCopy(envelope, headerLen + NonceSize + TagSize, cipherBytes, 0, cipherLen);

        var plainBytes = new byte[cipherLen];
        using var gcm = new AesGcm(key, TagSize);
        gcm.Decrypt(nonce, cipherBytes, tag, plainBytes);
        return Encoding.UTF8.GetString(plainBytes);
    }

    private async Task<byte[]> ResolveCurrentKeyAsync(string version, CancellationToken ct)
    {
        try
        {
            return await _keys.GetKeyAsync(_options.KeySecretPrefix, version, devConfigFallback: null, ct);
        }
        catch (InvalidOperationException ex)
        {
            throw new StaleEncryptionKeyException(version,
                $"Current key version '{version}' cannot be resolved. Cannot encrypt new identifiers.",
                ex);
        }
    }

    private async Task<byte[]> GetLegacyKeyAsync(CancellationToken ct)
    {
        if (_cachedLegacyKey != null) return _cachedLegacyKey;
        await _legacyKeyLock.WaitAsync(ct);
        try
        {
            if (_cachedLegacyKey != null) return _cachedLegacyKey;
            if (string.IsNullOrWhiteSpace(_options.LegacyKeySecretName))
                throw new CryptographicException(
                    "0x01 envelope encountered but MemberEncryption:LegacyKeySecretName is not configured.");

            var raw = await _secrets.GetSecretAsync(_options.LegacyKeySecretName, ct)
                ?? throw new CryptographicException(
                    $"0x01 envelope decrypt failed: legacy key '{_options.LegacyKeySecretName}' not found in secret provider.");

            byte[] keyBytes;
            try { keyBytes = Convert.FromBase64String(raw); }
            catch (FormatException) { keyBytes = Encoding.UTF8.GetBytes(raw); }

            _cachedLegacyKey = keyBytes;
            return keyBytes;
        }
        finally
        {
            _legacyKeyLock.Release();
        }
    }

    private static void EnsureKeyLength(byte[] key)
    {
        if (key.Length != 32)
            throw new InvalidOperationException(
                $"Identifier encryption key must be 32 bytes (AES-256); got {key.Length}.");
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
