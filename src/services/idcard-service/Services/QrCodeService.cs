using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloudHealthOffice.Infrastructure.Configuration;
using IdCardService.Models;
using QRCoder;

namespace IdCardService.Services;

/// <summary>
/// Generates + verifies HMAC-signed QR payloads. Keys are fetched from the
/// configured <see cref="ISecretProvider"/> under the name
/// <c>{SigningKeySecretPrefix}-{version}</c> so rotation is done by publishing
/// a new secret version and updating <c>IdCard:CurrentKeyVersion</c> — no code
/// change and no mass re-issuance. Verification accepts any version listed in
/// <c>IdCard:AcceptedKeyVersions</c>, so cards signed under older rolling
/// versions keep scanning until the window drops them.
/// </summary>
public class QrCodeService : IQrCodeService
{
    private readonly ISecretProvider _secrets;
    private readonly IConfiguration _configuration;
    private readonly ILogger<QrCodeService> _logger;

    // In-memory cache of resolved keys so every scan doesn't hit Key Vault.
    private readonly Dictionary<string, byte[]> _keyCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _keyLock = new(1, 1);

    private static readonly JsonSerializerOptions CanonicalJson = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = null
    };

    public QrCodeService(ISecretProvider secrets, IConfiguration configuration, ILogger<QrCodeService> logger)
    {
        _secrets = secrets;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<(byte[] PngBytes, string QrPayloadString, string KeyVersion, string CanonicalPayload)>
        GenerateAsync(string tenantId, string memberId, string cardId, DateTime issuedAt, CancellationToken ct = default)
    {
        var keyVersion = _configuration["IdCard:CurrentKeyVersion"] ?? "v1";
        var key = await GetKeyAsync(keyVersion, ct);

        var payload = new QrCardPayload
        {
            Version = 1,
            TenantId = tenantId,
            MemberId = memberId,
            CardId = cardId,
            IssuedAtUnix = new DateTimeOffset(issuedAt, TimeSpan.Zero).ToUnixTimeSeconds(),
            KeyVersion = keyVersion
        };

        var canonicalBytes = JsonSerializer.SerializeToUtf8Bytes(payload, CanonicalJson);
        var canonicalB64 = Base64UrlEncode(canonicalBytes);
        var signature = ComputeSignature(key, canonicalBytes);
        var sigB64 = Base64UrlEncode(signature);
        var qrPayloadString = $"{canonicalB64}.{sigB64}";

        var pixelsPerModule = _configuration.GetValue<int>("IdCard:Qr:PixelsPerModule", 6);
        var eccRaw = _configuration["IdCard:Qr:EccLevel"] ?? "Q";
        var ecc = eccRaw.ToUpperInvariant() switch
        {
            "L" => QRCodeGenerator.ECCLevel.L,
            "M" => QRCodeGenerator.ECCLevel.M,
            "H" => QRCodeGenerator.ECCLevel.H,
            _ => QRCodeGenerator.ECCLevel.Q
        };

        using var generator = new QRCodeGenerator();
        using var qrData = generator.CreateQrCode(qrPayloadString, ecc);
        using var pngQr = new PngByteQRCode(qrData);
        var png = pngQr.GetGraphic(pixelsPerModule);

        return (png, qrPayloadString, keyVersion, canonicalB64);
    }

    public async Task<(QrCardPayload? Payload, string? ErrorCode, string? ErrorMessage)>
        VerifyAsync(string qrPayloadString, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(qrPayloadString) || !qrPayloadString.Contains('.'))
        {
            return (null, ScanErrorCodes.MalformedPayload, "Payload is empty or missing signature segment");
        }

        var parts = qrPayloadString.Split('.', 2);
        if (parts.Length != 2)
        {
            return (null, ScanErrorCodes.MalformedPayload, "Payload does not have exactly two segments");
        }

        byte[] canonicalBytes;
        byte[] signatureBytes;
        try
        {
            canonicalBytes = Base64UrlDecode(parts[0]);
            signatureBytes = Base64UrlDecode(parts[1]);
        }
        catch (FormatException ex)
        {
            return (null, ScanErrorCodes.MalformedPayload, $"Base64url decode failed: {ex.Message}");
        }

        QrCardPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<QrCardPayload>(canonicalBytes, CanonicalJson);
        }
        catch (JsonException ex)
        {
            return (null, ScanErrorCodes.MalformedPayload, $"JSON parse failed: {ex.Message}");
        }
        if (payload == null)
        {
            return (null, ScanErrorCodes.MalformedPayload, "Null payload after parse");
        }

        var accepted = _configuration.GetSection("IdCard:AcceptedKeyVersions").Get<string[]>()
            ?? new[] { _configuration["IdCard:CurrentKeyVersion"] ?? "v1" };

        if (!accepted.Contains(payload.KeyVersion, StringComparer.OrdinalIgnoreCase))
        {
            return (null, ScanErrorCodes.StaleKey,
                $"Key version '{payload.KeyVersion}' is outside the accepted rolling window. Request a new card.");
        }

        byte[] key;
        try
        {
            key = await GetKeyAsync(payload.KeyVersion, ct);
        }
        catch (InvalidOperationException ex)
        {
            // Configured as accepted but secret missing — treat as stale so
            // the client gets a clear "request a new card" response.
            _logger.LogWarning(ex, "Accepted key version {Version} has no secret value", Sanitize(payload.KeyVersion));
            return (null, ScanErrorCodes.StaleKey, "Signing key unavailable for payload key version");
        }

        var expected = ComputeSignature(key, canonicalBytes);
        if (!CryptographicOperations.FixedTimeEquals(expected, signatureBytes))
        {
            return (null, ScanErrorCodes.InvalidSignature, "Signature mismatch");
        }

        return (payload, null, null);
    }

    private async Task<byte[]> GetKeyAsync(string version, CancellationToken ct)
    {
        if (_keyCache.TryGetValue(version, out var cached))
        {
            return cached;
        }

        await _keyLock.WaitAsync(ct);
        try
        {
            if (_keyCache.TryGetValue(version, out cached))
            {
                return cached;
            }

            var prefix = _configuration["IdCard:SigningKeySecretPrefix"] ?? "idcard-signing-key";
            var secretName = $"{prefix}-{version}";
            var raw = await _secrets.GetSecretAsync(secretName, ct);

            if (string.IsNullOrEmpty(raw))
            {
                // Fall back to configuration for dev/test environments so the
                // service can run without a Key Vault. Production wires
                // ISecretProvider to Key Vault and this branch will not hit.
                raw = _configuration[$"IdCard:DevSigningKeys:{version}"];
            }

            if (string.IsNullOrEmpty(raw))
            {
                throw new InvalidOperationException(
                    $"ID card signing key '{secretName}' is not configured. Publish the secret and/or update IdCard:AcceptedKeyVersions.");
            }

            byte[] keyBytes;
            try
            {
                keyBytes = Convert.FromBase64String(raw);
            }
            catch (FormatException)
            {
                // Accept UTF-8 strings in dev environments; production secrets
                // should be stored base64-encoded random bytes.
                keyBytes = Encoding.UTF8.GetBytes(raw);
            }

            _keyCache[version] = keyBytes;
            return keyBytes;
        }
        finally
        {
            _keyLock.Release();
        }
    }

    private static byte[] ComputeSignature(byte[] key, byte[] payload)
    {
        using var hmac = new HMACSHA256(key);
        return hmac.ComputeHash(payload);
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string input)
    {
        var padded = input.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2: padded += "=="; break;
            case 3: padded += "="; break;
            case 1: throw new FormatException("Invalid base64url length");
        }
        return Convert.FromBase64String(padded);
    }

    private static string Sanitize(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", "").Replace("\n", "");
}
