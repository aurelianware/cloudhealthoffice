namespace CloudHealthOffice.Portal.Services;

public class UpdateBenefitPlanRequest : CreateBenefitPlanRequest
{
    public string Status { get; set; } = string.Empty;
    public DateTime? TerminationDate { get; set; }
}
