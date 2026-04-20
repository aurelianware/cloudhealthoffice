using IdCardService.Models;

namespace IdCardService.Services;

public interface IQrCodeService
{
    /// <summary>
    /// Generates a PNG QR containing a signed payload for the card. Returns
    /// the PNG bytes, the on-wire payload string (<c>{canonical}.{sig}</c>),
    /// the key version used, and the canonical (unsigned) base64url segment
    /// that was persisted on the <see cref="IdCardRecord"/> for audit.
    /// </summary>
    Task<(byte[] PngBytes, string QrPayloadString, string KeyVersion, string CanonicalPayload)>
        GenerateAsync(string tenantId, string memberId, string cardId, DateTime issuedAt, CancellationToken ct = default);

    /// <summary>
    /// Parses and verifies a scanned QR payload. Returns the decoded payload on
    /// success or the <see cref="ScanErrorCodes"/> on failure.
    /// </summary>
    Task<(QrCardPayload? Payload, string? ErrorCode, string? ErrorMessage)>
        VerifyAsync(string qrPayloadString, CancellationToken ct = default);
}
