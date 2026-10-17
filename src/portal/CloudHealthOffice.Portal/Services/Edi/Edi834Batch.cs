namespace CloudHealthOffice.Portal.Services;

// ── PR14: EDI Transaction Operations ────────────────────────────────────────

public class Edi834Batch
{
    public string BatchId { get; set; } = string.Empty;
    public string TradingPartnerId { get; set; } = string.Empty;
    public string TradingPartnerName { get; set; } = string.Empty;
    public DateTime ReceivedDate { get; set; }
    public int TotalRecords { get; set; }
    public int AcceptedCount { get; set; }
    public int RejectedCount { get; set; }
    public int PendingCount { get; set; }
    public string Status { get; set; } = string.Empty; // Processing, Completed, Failed, PartiallyAccepted
    public string? OriginalFileName { get; set; }
}
