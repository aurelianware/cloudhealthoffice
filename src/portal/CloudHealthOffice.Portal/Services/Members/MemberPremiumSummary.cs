namespace CloudHealthOffice.Portal.Services;

public class MemberPremiumSummary
{
    public string MemberId { get; set; } = string.Empty;
    public PremiumInvoiceView? CurrentInvoice { get; set; }
    public DateTime? NextBillDate { get; set; }
    public bool AutopayEnabled { get; set; }
    public GracePeriodState Grace { get; set; } = new();
    public List<PremiumInvoiceView> Last12 { get; set; } = new();
}
