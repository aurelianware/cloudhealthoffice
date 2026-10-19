namespace CloudHealthOffice.Portal.Services;

public class BenefitPlan
{
    public string PlanId { get; set; } = string.Empty;
    public string PlanName { get; set; } = string.Empty;
    public string PlanType { get; set; } = string.Empty;
    public decimal Deductible { get; set; }
    public decimal OutOfPocketMax { get; set; }
}
