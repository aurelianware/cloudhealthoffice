namespace CloudHealthOffice.Portal.Services;

public class UpsertPlanBenefitRequest
{
    public string BenefitType { get; set; } = "medical";
    public string ServiceCategory { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool IsCovered { get; set; } = true;
    public List<string> CptCodes { get; set; } = new();
    public decimal? InNetworkCopay { get; set; }
    public decimal? OutNetworkCopay { get; set; }
    public decimal? InNetworkCoinsurancePercent { get; set; }
    public decimal? OutNetworkCoinsurancePercent { get; set; }
    public bool DeductibleApplies { get; set; } = true;
    public bool OopApplies { get; set; } = true;
    public bool PriorAuthRequired { get; set; }
    public int? VisitLimit { get; set; }
    public string? VisitLimitPeriod { get; set; }
}
