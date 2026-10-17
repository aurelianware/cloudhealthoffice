namespace CloudHealthOffice.Portal.Services;

public class CapLineItem
{
    public string MemberId { get; set; } = string.Empty;
    public string MemberName { get; set; } = string.Empty;
    public string? PlanId { get; set; }
    public int MemberAge { get; set; }
    public string? Gender { get; set; }
    public decimal BasePMPM { get; set; }
    public decimal RiskScore { get; set; } = 1.0m;
    public decimal AdjustedPMPM { get; set; }
    public decimal ProrationFactor { get; set; } = 1.0m;
    public decimal GrossAmount { get; set; }
    public decimal WithholdAmount { get; set; }
    public decimal NetAmount { get; set; }
    public bool IsRetroactive { get; set; }
    public string? AdjustmentReason { get; set; }
}
