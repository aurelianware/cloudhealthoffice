namespace CloudHealthOffice.Portal.Services;

public class MemberArSummary
{
    public string MemberId { get; set; } = string.Empty;
    public decimal CurrentBalance { get; set; }
    public AgedBuckets Aged { get; set; } = new();
    public List<ArChargeRow> RecentCharges { get; set; } = new();
    public List<ArPaymentRow> RecentPayments { get; set; } = new();
    public DateTime AsOfUtc { get; set; }
}
