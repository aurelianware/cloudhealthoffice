using System.Text.Json.Serialization;

namespace CloudHealthOffice.Portal.Services;

public class ClaimLineAdjustment
{
    public string GroupCode { get; set; } = string.Empty;
    public string ReasonCode { get; set; } = string.Empty;
    [JsonConverter(typeof(FlexibleDecimalJsonConverter))]
    public decimal Amount { get; set; }
    public string Description { get; set; } = string.Empty;
    /// <summary>The RARC qualifying the adjustment, when there is one.</summary>
    public string? RemarkCode { get; set; }
}
