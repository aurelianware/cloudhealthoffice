namespace CloudHealthOffice.Portal.Services;

public class MassAdjudicationFailureSummary
{
    public string GeneratedClaimId { get; set; } = string.Empty;
    public string? Stage { get; set; }
    public string? Error { get; set; }
}
