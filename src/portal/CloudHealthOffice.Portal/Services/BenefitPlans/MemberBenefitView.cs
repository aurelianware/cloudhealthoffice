namespace CloudHealthOffice.Portal.Services;

public class MemberBenefitView
{
    public string PlanId { get; set; } = string.Empty;
    public string PlanName { get; set; } = string.Empty;
    public string Payer { get; set; } = string.Empty;
    public string PlanType { get; set; } = string.Empty;
    public string? MetalLevel { get; set; }
    public string LineOfBusiness { get; set; } = string.Empty;
    public DateTime AsOfDate { get; set; }
    public DateTime EffectiveDate { get; set; }
    public DateTime? TerminationDate { get; set; }
    public string PlanVersion { get; set; } = string.Empty;
    public string FamilyAccumulatorModel { get; set; } = "Embedded";
    public MemberBenefitCostSharing CostSharing { get; set; } = new();
    public List<CategorizedBenefit> Categories { get; set; } = new();
    public List<PlanDocumentLink> Documents { get; set; } = new();
}
