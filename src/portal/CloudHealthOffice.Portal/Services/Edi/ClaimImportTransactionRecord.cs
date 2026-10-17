namespace CloudHealthOffice.Portal.Services;

/// <summary>Portal projection of claims-service's ClaimImportTransaction.</summary>
public class ClaimImportTransactionRecord
{
    public string Id { get; set; } = string.Empty;
    public string ClaimNumber { get; set; } = string.Empty;
    public string? ClaimId { get; set; }
    public string MemberId { get; set; } = string.Empty;
    public string? FileName { get; set; }
    public DateTime ReceivedAt { get; set; }
    public string Status { get; set; } = string.Empty;
    public List<string> Errors { get; set; } = new();
}
