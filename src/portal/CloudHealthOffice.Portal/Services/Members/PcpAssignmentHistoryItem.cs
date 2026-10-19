namespace CloudHealthOffice.Portal.Services;

public class PcpAssignmentHistoryItem
{
    public string Id { get; set; } = string.Empty;
    public string MemberId { get; set; } = string.Empty;
    public string CoverageId { get; set; } = string.Empty;
    public string ProviderNpi { get; set; } = string.Empty;
    public string? ProviderId { get; set; }
    public string? ProviderName { get; set; }
    public DateTime EffectiveDate { get; set; }
    public DateTime? EndDate { get; set; }
    public string? AssignmentReason { get; set; }
    public string AssignmentSource { get; set; } = "MemberChoice";
    public string NetworkStatusAtAssignment { get; set; } = "Unknown";
    public string? AssignedBy { get; set; }
    public DateTime CreatedDate { get; set; }
}
