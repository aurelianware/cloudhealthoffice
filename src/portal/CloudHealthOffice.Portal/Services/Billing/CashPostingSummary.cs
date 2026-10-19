namespace CloudHealthOffice.Portal.Services;

public class CashPostingSummary
{
    public string Id { get; set; } = string.Empty;
    public string PostingNumber { get; set; } = string.Empty;
    public DateTime ReceiptDate { get; set; }
    public decimal Amount { get; set; }
    public string PaymentMethod { get; set; } = "Check";
    public string? CheckNumber { get; set; }
    public string? BankReference { get; set; }
    public string PayerType { get; set; } = "Sponsor";
    public string PayerReferenceId { get; set; } = string.Empty;
    public string? PayerName { get; set; }
    public decimal AppliedAmount { get; set; }
    public decimal UnappliedAmount { get; set; }
    public string Status { get; set; } = "Pending";
}
