using System.Text.Json.Serialization;

namespace CloudHealthOffice.Portal.Services;

public class IdCardOrderView
{
    public string OrderId { get; set; } = string.Empty;
    [JsonConverter(typeof(FlexibleClaimStatusJsonConverter))]
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
