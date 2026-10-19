namespace CloudHealthOffice.Portal.Services;

public class PaymentRunClaimItem
{
    public string ClaimId { get; set; } = string.Empty;
    public string ClaimNumber { get; set; } = string.Empty;
    public string MemberName { get; set; } = string.Empty;
    public string ProviderName { get; set; } = string.Empty;
    public decimal ChargeAmount { get; set; }
    public decimal AllowedAmount { get; set; }
    public decimal PaidAmount { get; set; }
    public decimal MemberResponsibility { get; set; }
    public string PaymentStatus { get; set; } = string.Empty; // Included, Excluded, Adjusted
}
