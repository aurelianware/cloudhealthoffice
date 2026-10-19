namespace CloudHealthOffice.Portal.Services;

public class ArPaymentRow
{
    public string EntryId { get; set; } = string.Empty;
    public DateTime PostedAt { get; set; }
    public decimal Amount { get; set; }
    public string Source { get; set; } = string.Empty;
    public string? SourceReferenceNumber { get; set; }
    public string? Memo { get; set; }
}
