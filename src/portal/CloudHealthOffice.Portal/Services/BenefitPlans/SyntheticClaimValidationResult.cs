namespace CloudHealthOffice.Portal.Services;

public class SyntheticClaimValidationResult
{
    public string ClaimId { get; set; } = string.Empty;
    public string ClaimNumber { get; set; } = string.Empty;
    public string MemberId { get; set; } = string.Empty;
    public string ExpectedPlanId { get; set; } = string.Empty;
    public string? ResolvedPlanId { get; set; }
    public string PlanVersion { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? NetworkTier { get; set; }
    public decimal ChargeAmount { get; set; }
    public decimal AllowedAmount { get; set; }
    public decimal DeductibleAmount { get; set; }
    public decimal CopayAmount { get; set; }
    public decimal CoinsuranceAmount { get; set; }
    public decimal PatientResponsibility { get; set; }
    public decimal PaidAmount { get; set; }
    public string? OutcomeReason { get; set; }
    public TimeSpan Elapsed { get; set; }
    public bool ExactPlanMatched => string.Equals(ExpectedPlanId, ResolvedPlanId, StringComparison.Ordinal);
}
