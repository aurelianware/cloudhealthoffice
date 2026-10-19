namespace CloudHealthOffice.Portal.Services;

public class PremiumInvoiceView
{
    public string Id { get; set; } = string.Empty;
    public string InvoiceNumber { get; set; } = string.Empty;
    public string GroupNumber { get; set; } = string.Empty;
    public string SponsorName { get; set; } = string.Empty;
    public DateTime BillingPeriodStart { get; set; }
    public DateTime BillingPeriodEnd { get; set; }
    public DateTime DueDate { get; set; }
    /// <summary>InvoiceStatus enum name.</summary>
    public string Status { get; set; } = string.Empty;
    public decimal TotalAmount { get; set; }
    public decimal TotalPaid { get; set; }
    public decimal BalanceDue { get; set; }
    public bool IsAptcSubsidized { get; set; }
    public decimal AptcMonthlyAmount { get; set; }
    /// <summary>"Standard" or "AptcThreeMonth".</summary>
    public string GraceType { get; set; } = "Standard";
}
