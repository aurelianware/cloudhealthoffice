namespace CloudHealthOffice.Portal.Services;

public class BenefitPlanValidationResult
{
    public DateTime ServiceDate { get; set; }
    public string PlanVersion { get; set; } = string.Empty;
    public MemberBenefitView? MemberView { get; set; }
    public List<BenefitPlanValidationCheck> Checks { get; set; } = new();
    public bool IsValid => Checks.All(check => check.Severity != "Error");
}
