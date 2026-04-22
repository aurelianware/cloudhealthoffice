using System.Security.Cryptography;
using System.Text;
using CloudHealthOffice.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;

namespace ConsentService.Services;

/// <summary>
/// AES-256-GCM encryptor for consent body fields. Sibling of
/// <c>MemberService.Services.KeyVaultIdentifierEncryptor</c> rather than a
/// refactor — consent-service is greenfield, so there is NO 0x01 legacy
/// envelope path. Only 0x02 envelopes are read and written.
///
/// Envelope format (base64url of):
///   0x02: [0x02][keyVerLen=1 byte][keyVer UTF-8 bytes][12 IV][16 tag][ciphertext]
/// </summary>
public sealed class ConsentFieldEncryptor : IConsentFieldEncryptor
{
    private const byte FormatV2 = 0x02;
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly RotatingKeyProvider _keys;
    private readonly ILogger<ConsentFieldEncryptor> _logger;
    private readonly ConsentEncryptionOptions _options;

    public ConsentFieldEncryptor(
        RotatingKeyProvider keys,
        ILogger<ConsentFieldEncryptor> logger,
        ConsentEncryptionOptions options)
    {
        _keys = keys;
        _logger = logger;
        _options = options;

        if (string.IsNullOrWhiteSpace(options.KeySecretPrefix))
            throw new ArgumentException("KeySecretPrefix is required", nameof(options));
        if (string.IsNullOrWhiteSpace(options.CurrentKeyVersion))
            throw new ArgumentException("CurrentKeyVersion is required", nameof(options));
        if (options.AcceptedKeyVersions.Count == 0)
            throw new ArgumentException("AcceptedKeyVersions must contain at least one entry", nameof(options));
    }

    public async Task<string?> EncryptAsync(string? plaintext, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(plaintext)) return plaintext;

        var keyVersion = _options.CurrentKeyVersion;
        var keyVersionBytes = Encoding.UTF8.GetBytes(keyVersion);
        if (keyVersionBytes.Length == 0 || keyVersionBytes.Length > 255)
            throw new InvalidOperationException(
                $"ConsentEncryption:CurrentKeyVersion '{keyVersion}' must be 1..255 UTF-8 bytes.");

        var key = await ResolveKeyAsync(keyVersion, ct);
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
            _logger.LogWarning(ex, "Consent ciphertext is not valid base64url");
            throw new CryptographicException("Consent ciphertext is not valid base64url", ex);
        }

        if (envelope.Length < 1)
            throw new CryptographicException("Envelope is empty");

        if (envelope[0] != FormatV2)
            throw new CryptographicException($"Unsupported envelope version 0x{envelope[0]:X2}");

        if (envelope.Length < 1 + 1) throw new CryptographicException("0x02 envelope too short");
        int keyVerLen = envelope[1];
        var headerLen = 1 + 1 + keyVerLen;
        if (envelope.Length < headerLen + NonceSize + TagSize)
            throw new CryptographicException("0x02 envelope too short for key-version + IV + tag");

        var keyVersion = Encoding.UTF8.GetString(envelope, 2, keyVerLen);

        if (!_options.AcceptedKeyVersions.Contains(keyVersion, StringComparer.OrdinalIgnoreCase))
            throw new CryptographicException(
                $"Envelope key version '{keyVersion}' is not in ConsentEncryption:AcceptedKeyVersions. " +
                "Either widen the accepted window or re-encrypt the record.");

        var key = await ResolveKeyAsync(keyVersion, ct);
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

    private async Task<byte[]> ResolveKeyAsync(string version, CancellationToken ct)
    {
        try
        {
            return await _keys.GetKeyAsync(_options.KeySecretPrefix, version, devConfigFallback: null, ct);
        }
        catch (InvalidOperationException ex)
        {
            throw new StaleEncryptionKeyException(version,
                $"Key version '{version}' cannot be resolved from the secret provider. " +
                "Publish the secret or drop the version from AcceptedKeyVersions.",
                ex);
        }
    }

    private static void EnsureKeyLength(byte[] key)
    {
        if (key.Length != 32)
            throw new InvalidOperationException(
                $"Consent encryption key must be 32 bytes (AES-256); got {key.Length}.");
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

/// <summary>
/// Dev-only passthrough. Used when <see cref="ConsentEncryptionOptions"/> is
/// not configured and the host is <c>IsDevelopment</c>. A non-dev startup
/// with no <c>ConsentEncryption</c> section throws in <c>Program.cs</c>
/// rather than falling back to plaintext.
/// </summary>
public sealed class NoOpConsentFieldEncryptor : IConsentFieldEncryptor
{
    public Task<string?> EncryptAsync(string? plaintext, CancellationToken ct = default) => Task.FromResult(plaintext);
    public Task<string?> DecryptAsync(string? ciphertext, CancellationToken ct = default) => Task.FromResult(ciphertext);
}
