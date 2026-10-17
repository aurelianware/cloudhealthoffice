namespace CloudHealthOffice.Portal.Services;

public class TermMapVersionSummary
{
    public string Id { get; set; } = string.Empty;
    public string MapName { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string SourceSystem { get; set; } = string.Empty;
    public string TargetSystem { get; set; } = string.Empty;
    public DateTime ImportedAt { get; set; }
    public bool IsActive { get; set; }
    public int EntryCount { get; set; }
    public string? SourceChecksum { get; set; }
}
