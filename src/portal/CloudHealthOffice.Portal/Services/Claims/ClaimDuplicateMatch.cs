namespace CloudHealthOffice.Portal.Services;

/// <summary>
/// One prior claim a DUPLICATE-pended claim matched, from claims-service
/// <c>GET /claims/{id}/duplicate-matches</c>. Shown in the claim-detail
/// "Possible duplicates" panel.
/// </summary>
public class ClaimDuplicateMatch
{
    public string MatchedClaimId { get; set; } = string.Empty;
    public string? MatchedClaimNumber { get; set; }
    /// <summary>"Exact" or "Suspect".</summary>
    public string MatchType { get; set; } = string.Empty;
    public bool MatchedClaimFound { get; set; }
    public DateTime? ServiceDateFrom { get; set; }
    public DateTime? ServiceDateTo { get; set; }
    public decimal? BilledAmount { get; set; }
    public string? Status { get; set; }
    public List<string> MatchedFields { get; set; } = new();
    public List<ClaimDuplicateLineMatch> Lines { get; set; } = new();
}
