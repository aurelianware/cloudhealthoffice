namespace CloudHealthOffice.Portal.Services;

/// <summary>Portal projection of enrollment-import-service's EnrollmentImportRun — one row per 834 batch.</summary>
public class EnrollmentImportRunRecord
{
    public string Id { get; set; } = string.Empty;
    public string BatchId { get; set; } = string.Empty;
    public string? FileName { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public int SuccessCount { get; set; }
    public int FailedCount { get; set; }
    public int SkippedCount { get; set; }
    public int MembersCreated { get; set; }
    public int MembersUpdated { get; set; }
    public int MembersTerminated { get; set; }
    public int DependentsCreated { get; set; }
    public int CoverageRecordsCreated { get; set; }
    public int CoverageMappingsUnresolved { get; set; }
    public List<string> Errors { get; set; } = new();
}
