namespace CloudHealthOffice.Portal.Services;

public class ClaimAdjudicationProjection
{
    public string? NetworkTier { get; set; }
    public decimal AllowedAmount { get; set; }
    public decimal DeductibleAmount { get; set; }
    public decimal CoinsuranceAmount { get; set; }
    public decimal CopayAmount { get; set; }
    public decimal PatientResponsibility { get; set; }
    public decimal PayerPayment { get; set; }
    public string? CheckNumber { get; set; }
    public DateTime? PaymentDate { get; set; }
    public string? DenialReason { get; set; }
}
