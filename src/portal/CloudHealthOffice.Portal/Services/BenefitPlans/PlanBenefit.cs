namespace CloudHealthOffice.Portal.Services;

public class PlanBenefit
{
    public string BenefitId { get; set; } = string.Empty;
    public string BenefitType { get; set; } = "medical";
    public string ServiceCategory { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool IsCovered { get; set; } = true;
    public string ServiceType { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public decimal? Copay { get; set; }
    public decimal? CoinsurancePercent { get; set; }
    public decimal? OutNetworkCopay { get; set; }
    public decimal? OutNetworkCoinsurancePercent { get; set; }
    public decimal? CoveragePercent { get; set; }
    public int? AnnualLimit { get; set; }
    public string? VisitLimitPeriod { get; set; }
    public bool DeductibleApplies { get; set; }
    public bool OopApplies { get; set; } = true;
    public bool PriorAuthRequired { get; set; }
    public List<string> CptCodes { get; set; } = new();
}
