using System.Text.Json.Serialization;

namespace IdCardService.Models;

public class IdCardOrder
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string TenantId { get; set; } = string.Empty;
    public string MemberId { get; set; } = string.Empty;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public IdCardDeliveryChannel Channel { get; set; } = IdCardDeliveryChannel.Digital;

    public string? LanguageCode { get; set; }
    public string? TemplateId { get; set; }
    public string RequestedBy { get; set; } = "system";

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public IdCardOrderStatus Status { get; set; } = IdCardOrderStatus.Pending;

    public string? FailureReason { get; set; }
    public string? FailureCode { get; set; }

    public string? CardId { get; set; }
    public string? DocumentId { get; set; }
    public string? PreviewDocumentId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? IssuedAt { get; set; }

    /// <summary>Adapter platform that handled this order (cho, qnxt, fulfillment-vendor).</summary>
    public string Platform { get; set; } = "cho";
}
