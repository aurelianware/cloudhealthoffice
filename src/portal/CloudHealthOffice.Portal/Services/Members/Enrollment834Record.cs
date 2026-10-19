namespace CloudHealthOffice.Portal.Services;

public class Enrollment834Record
{
    public string TransactionId { get; set; } = string.Empty;
    public string BatchId { get; set; } = string.Empty;
    public string MemberId { get; set; } = string.Empty;
    public string MemberName { get; set; } = string.Empty;
    public string MaintenanceTypeCode { get; set; } = string.Empty; // 001=Change, 021=Add, 024=Cancel
    public string MaintenanceReasonCode { get; set; } = string.Empty;
    public string TransactionSetPurpose { get; set; } = string.Empty;
    public DateTime TransactionDate { get; set; }
    public string Status { get; set; } = string.Empty; // Accepted, Rejected, Pending
    public List<string> Errors { get; set; } = new();
    public string? RawSegmentPreview { get; set; }
}
