namespace CloudHealthOffice.Portal.Services;

public class EdiTransactionHistoryItem
{
    public string TransactionId { get; set; } = string.Empty;
    public string TransactionType { get; set; } = string.Empty; // 834, 835, 277CA, 270, 271, 278
    public DateTime TransactionDate { get; set; }
    public string TradingPartnerId { get; set; } = string.Empty;
    public string TradingPartnerName { get; set; } = string.Empty;
    public string Direction { get; set; } = string.Empty; // Inbound, Outbound
    public string Status { get; set; } = string.Empty;
    public string? ErrorSummary { get; set; }
    public int RecordCount { get; set; }
}
