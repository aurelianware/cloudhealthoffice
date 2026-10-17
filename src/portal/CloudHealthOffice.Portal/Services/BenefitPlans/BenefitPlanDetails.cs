namespace CloudHealthOffice.Portal.Services;

public class BenefitPlanDetails : BenefitPlanListItem
{
    public string VersionId { get; set; } = string.Empty;
    public int VersionNumber { get; set; }
    public string VersionState { get; set; } = string.Empty;
    public string MetalTier { get; set; } = string.Empty; // Bronze, Silver, Gold, Platinum
    public decimal IndividualDeductible { get; set; }
    public decimal FamilyDeductible { get; set; }
    public decimal IndividualOOPMax { get; set; }
    public decimal FamilyOOPMax { get; set; }
    public decimal Coinsurance { get; set; }
    public string PlanYear { get; set; } = string.Empty;
    public List<PlanBenefit> Benefits { get; set; } = new();
    public List<PlanBenefit> Exclusions { get; set; } = new();
    public List<PlanNetworkTier> NetworkTiers { get; set; } = new();
}
