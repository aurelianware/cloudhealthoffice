using System.Text.Json.Serialization;

namespace IdCardService.Models;

/// <summary>
/// Canonical structure of the data encoded in the card QR. The on-wire form is
/// <c>{canonical_base64url}.{signature_base64url}</c>; the canonical part
/// serializes this type with ordered properties so that the same bytes are
/// signed and verified.
/// </summary>
public class QrCardPayload
{
    [JsonPropertyName("v")]
    public int Version { get; set; } = 1;

    [JsonPropertyName("t")]
    public string TenantId { get; set; } = string.Empty;

    [JsonPropertyName("m")]
    public string MemberId { get; set; } = string.Empty;

    [JsonPropertyName("c")]
    public string CardId { get; set; } = string.Empty;

    [JsonPropertyName("i")]
    public long IssuedAtUnix { get; set; }

    /// <summary>Key version used to sign this payload (e.g. "v1").</summary>
    [JsonPropertyName("k")]
    public string KeyVersion { get; set; } = "v1";
}

/// <summary>
/// Response returned by <c>POST /api/v1/id-cards/scan</c>: the resolved card
/// plus an as-of-today 271 snapshot from eligibility-service.
/// </summary>
public class QrScanResponse
{
    public string CardId { get; set; } = string.Empty;
    public string MemberId { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public DateTime IssuedAt { get; set; }
    public DateTime ScannedAt { get; set; }
    public bool CardActive { get; set; }
    public bool CoverageActive { get; set; }

    /// <summary>
    /// 271 eligibility snapshot (normalized response from eligibility-service
    /// <c>POST /api/eligibility/inquiry</c>). Shape is passed through unchanged.
    /// </summary>
    public object? EligibilitySnapshot { get; set; }
}

/// <summary>Structured error codes returned by the scan endpoint.</summary>
public static class ScanErrorCodes
{
    public const string MalformedPayload = "CARD_PAYLOAD_MALFORMED";
    public const string InvalidSignature = "CARD_SIGNATURE_INVALID";
    public const string StaleKey = "CARD_SIGNATURE_STALE";
    public const string UnknownCard = "CARD_NOT_FOUND";
    public const string Revoked = "CARD_REVOKED";
    public const string CoverageInactive = "COVERAGE_INACTIVE";
    public const string RateLimited = "RATE_LIMIT_EXCEEDED";
}
