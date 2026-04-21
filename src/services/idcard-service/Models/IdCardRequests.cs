using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace IdCardService.Models;

public class CreateIdCardOrderRequest
{
    [Required]
    public string MemberId { get; set; } = string.Empty;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public IdCardDeliveryChannel Channel { get; set; } = IdCardDeliveryChannel.Digital;

    public string? LanguageCode { get; set; }
    public string? RequestedBy { get; set; }
}

public class IdCardOrderResponse
{
    public string OrderId { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? CardId { get; set; }
    public string? DocumentId { get; set; }
    public string? PreviewDocumentId { get; set; }
    public string? FailureReason { get; set; }
    public string? FailureCode { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? IssuedAt { get; set; }
}

public class IdCardHistoryEntry
{
    public string CardId { get; set; } = string.Empty;
    public string OrderId { get; set; } = string.Empty;
    public string DocumentId { get; set; } = string.Empty;
    public string? PreviewDocumentId { get; set; }
    public string? PlanId { get; set; }
    public string? SponsorId { get; set; }
    public string? LanguageCode { get; set; }
    public DateTime IssuedAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public string? RevocationReason { get; set; }
    public long ScanCount { get; set; }
}

public class QrScanRequest
{
    [Required]
    public string QrPayload { get; set; } = string.Empty;

    /// <summary>Optional provider NPI, flowed through to eligibility-service.</summary>
    public string? ProviderNpi { get; set; }
}

public class RevokeIdCardRequest
{
    [Required]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public IdCardRevocationReason Reason { get; set; }

    public string? Notes { get; set; }
}
