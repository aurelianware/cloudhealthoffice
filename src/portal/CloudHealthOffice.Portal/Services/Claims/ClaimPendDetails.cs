namespace CloudHealthOffice.Portal.Services;

public class ClaimPendDetails
{
    public string PendCode { get; set; } = string.Empty;
    public string PendReason { get; set; } = string.Empty;
    public DateTime PendedAt { get; set; }
    public List<ClaimPendEditFailure> EditFailures { get; set; } = new();
    public List<ClaimPendDuplicateFinding> DuplicateFindings { get; set; } = new();

    /// <summary>Further pend reasons ("{code}: {reason}") on the same claim.</summary>
    public List<string> AdditionalPendReasons { get; set; } = new();

    /// <summary>
    /// claims-service's fingerprint of exactly these pends: sent back with an
    /// approval, so it applies only to what the examiner viewed.
    /// </summary>
    public string? Fingerprint { get; set; }
}
