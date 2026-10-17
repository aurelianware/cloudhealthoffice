namespace CloudHealthOffice.Portal.Services;

public class IdCardHistoryView
{
    public string CardId { get; set; } = string.Empty;
    public string OrderId { get; set; } = string.Empty;
    public string DocumentId { get; set; } = string.Empty;
    public string? PreviewDocumentId { get; set; }
    public string? PlanId { get; set; }
    public string? SponsorId { get; set; }
    public string? LanguageCode { get; set; }
    public DateTime IssuedAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public string? RevocationReason { get; set; }
    public long ScanCount { get; set; }
}
