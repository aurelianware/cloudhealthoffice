namespace CloudHealthOffice.Portal.Services;

public class EraSummary
{
    public string EraId { get; set; } = string.Empty;
    public string PaymentId { get; set; } = string.Empty;
    public string PayerName { get; set; } = string.Empty;
    public string PayeeNPI { get; set; } = string.Empty;
    public string PayeeName { get; set; } = string.Empty;
    public DateTime PaymentDate { get; set; }
    public string PaymentMethod { get; set; } = string.Empty; // ACH, CHK
    public string CheckNumber { get; set; } = string.Empty;
    public decimal TotalPaymentAmount { get; set; }
    public int ClaimCount { get; set; }
    public string Status { get; set; } = string.Empty; // Generated, Transmitted, Acknowledged
}
