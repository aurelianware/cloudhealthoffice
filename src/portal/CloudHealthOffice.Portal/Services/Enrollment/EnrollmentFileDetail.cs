namespace CloudHealthOffice.Portal.Services;

public class EnrollmentFileDetail
{
    public string FileId { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public DateTime ReceivedTime { get; set; }
    public string SponsorName { get; set; } = string.Empty;
    public string GroupNumber { get; set; } = string.Empty;
    public int TransactionCount { get; set; }
    public int AddedCount { get; set; }
    public int TermedCount { get; set; }
    public int ChangedCount { get; set; }
    public int RejectedCount { get; set; }
    public string Status { get; set; } = string.Empty;
    public List<EnrollmentRejection> Rejections { get; set; } = new();
}
