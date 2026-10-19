namespace CloudHealthOffice.Portal.Services;

public class CapAdjustment
{
    public string Type { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string? RelatedMemberId { get; set; }
    public DateTime AdjustmentDate { get; set; }
}
