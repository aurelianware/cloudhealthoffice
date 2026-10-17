using System.Text.Json.Serialization;

namespace CloudHealthOffice.Portal.Services;

public class ClaimAdjustmentInfo
{
    public string AdjustmentType { get; set; } = string.Empty; // Reversal, Adjustment, Correction
    public string? OriginalClaimId { get; set; }
    public string? RelatedClaimId { get; set; }
    [JsonConverter(typeof(FlexibleDecimalJsonConverter))]
    public decimal AdjustmentAmount { get; set; }
    public string? Reason { get; set; }
    public DateTime? AdjustmentDate { get; set; }
    public string? AdjustedBy { get; set; }
}
