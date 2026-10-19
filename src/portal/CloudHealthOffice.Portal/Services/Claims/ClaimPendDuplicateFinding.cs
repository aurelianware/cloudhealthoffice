namespace CloudHealthOffice.Portal.Services;

/// <summary>
/// Portal projection of claims-service's <c>DuplicateFindingSnapshot</c>:
/// one line the duplicate-claim stage flagged and the prior line it matched.
/// </summary>
public class ClaimPendDuplicateFinding
{
    public string DuplicateType { get; set; } = string.Empty;
    public string RuleId { get; set; } = string.Empty;
    public string? Message { get; set; }
    public int LineNumber { get; set; }
    public string? MatchedClaimId { get; set; }
    public string? MatchedClaimNumber { get; set; }
    public int? MatchedLineNumber { get; set; }
}
