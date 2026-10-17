namespace CloudHealthOffice.Portal.Services;

public class PaymentRunDetails : PaymentRunSummary
{
    public DateTime ClaimServiceDateFrom { get; set; }
    public DateTime ClaimServiceDateTo { get; set; }
    public string? SponsorFilter { get; set; }
    public string? PlanFilter { get; set; }
    public List<PaymentRunClaimItem> Claims { get; set; } = new();
    public decimal TotalCharges { get; set; }
    public decimal TotalAllowed { get; set; }
    public decimal TotalMemberResponsibility { get; set; }
    public int ApprovedCount { get; set; }
    public int DeniedCount { get; set; }
    public int AdjustmentCount { get; set; }
}
