using System.Text.Json.Serialization;

namespace IdCardService.Models;

/// <summary>
/// An issued ID card. Immutable except for revocation. The QR payload embeds
/// <see cref="CardId"/>; revocation flips <see cref="RevokedAt"/> and future scans
/// return 410 Gone with the revocation reason.
/// </summary>
public class IdCardRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string TenantId { get; set; } = string.Empty;
    public string MemberId { get; set; } = string.Empty;
    public string OrderId { get; set; } = string.Empty;

    /// <summary>Adapter platform that issued the card (cho, qnxt, fulfillment-vendor).</summary>
    public string Platform { get; set; } = "cho";

    /// <summary>Opaque card identifier embedded in the QR payload.</summary>
    public string CardId { get; set; } = Guid.NewGuid().ToString("N");

    public string TemplateId { get; set; } = string.Empty;
    public string? SponsorId { get; set; }
    public string? PlanId { get; set; }
    public string? LanguageCode { get; set; }

    /// <summary>member-document-service DocumentReference id for the PDF.</summary>
    public string DocumentId { get; set; } = string.Empty;
    public string? PreviewDocumentId { get; set; }

    /// <summary>Key version used to sign the QR payload (e.g. "v1").</summary>
    public string KeyVersion { get; set; } = "v1";

    /// <summary>Base64url canonical payload (without signature) for audit.</summary>
    public string QrCanonicalPayload { get; set; } = string.Empty;

    public DateTime IssuedAt { get; set; } = DateTime.UtcNow;
    public DateTime? RevokedAt { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public IdCardRevocationReason? RevocationReason { get; set; }

    /// <summary>Actor that triggered revocation (user id, service account).</summary>
    public string? RevokedBy { get; set; }

    /// <summary>Free-form operator notes captured at revocation time.</summary>
    public string? RevocationNotes { get; set; }

    public long ScanCount { get; set; }
    public DateTime? LastScannedAt { get; set; }
}

public enum IdCardRevocationReason
{
    Replaced = 0,
    Lost = 1,
    Compromised = 2,
    CoverageEnded = 3,
    Administrative = 4
}
