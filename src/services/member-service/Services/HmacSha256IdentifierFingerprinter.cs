using System.Security.Cryptography;
using System.Text;
using CloudHealthOffice.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;

namespace MemberService.Services;

/// <summary>
/// HMAC-SHA256 fingerprinter keyed by a secret fetched from the configured
/// <see cref="ISecretProvider"/>. Fingerprint key MUST be distinct from the
/// AES-GCM encryption key so rotation does not leak plaintext identifiers.
/// </summary>
public sealed class HmacSha256IdentifierFingerprinter : IIdentifierFingerprinter
{
    private readonly ISecretProvider _secrets;
    private readonly ILogger<HmacSha256IdentifierFingerprinter> _logger;
    private readonly string _keySecretName;

    private byte[]? _cachedKey;
    private readonly SemaphoreSlim _keyLock = new(1, 1);

    public HmacSha256IdentifierFingerprinter(
        ISecretProvider secrets,
        ILogger<HmacSha256IdentifierFingerprinter> logger,
        string keySecretName)
    {
        _secrets = secrets;
        _logger = logger;
        _keySecretName = string.IsNullOrWhiteSpace(keySecretName)
            ? throw new ArgumentException("keySecretName is required", nameof(keySecretName))
            : keySecretName;
    }

    public bool IsEnabled => true;

    public async Task<string> FingerprintAsync(string normalizedPlaintext, CancellationToken ct = default)
    {
        var key = await GetKeyAsync(ct);
        var data = Encoding.UTF8.GetBytes(normalizedPlaintext ?? string.Empty);
        var hash = HMACSHA256.HashData(key, data);
        return Convert.ToHexString(hash);
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
                    $"Identifier fingerprint HMAC key '{_keySecretName}' not found in secret provider.");

            byte[] keyBytes;
            try
            {
                keyBytes = Convert.FromBase64String(raw);
            }
            catch (FormatException)
            {
                keyBytes = Encoding.UTF8.GetBytes(raw);
            }

            if (keyBytes.Length < 32)
                throw new InvalidOperationException(
                    $"Identifier fingerprint HMAC key must be at least 32 bytes; got {keyBytes.Length}.");

            _cachedKey = keyBytes;
            return keyBytes;
        }
        finally
        {
            _keyLock.Release();
        }
    }
}
