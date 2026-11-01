namespace CloudHealthOffice.Portal.Services;

public interface IClaimsService
{
    Task<List<ClaimSummary>> GetRecentClaimsAsync(int count);
    Task<ClaimSearchResult> SearchClaimsAsync(ClaimSearchRequest request);

    /// <summary>Claims Insights for claims adjudicated in [from, to), and open inventory now (claims:read).</summary>
    Task<ClaimInsightsView> GetClaimInsightsAsync(DateTime from, DateTime to, string? lineOfBusiness = null);
    Task<ClaimDetails?> GetClaimByIdAsync(string claimId);

    /// <summary>The claims standard reports (reports:claims).</summary>
    Task<List<ReportDefinitionView>> GetClaimReportCatalogAsync();

    /// <summary>Runs a claims report; <see cref="ReportForbiddenException"/> without reports:claims.</summary>
    Task<ReportRunView> RunClaimReportAsync(string reportId, IReadOnlyDictionary<string, string?> parameters);

    /// <summary>The same run as a CSV file.</summary>
    Task<ReportFile> DownloadClaimReportCsvAsync(string reportId, IReadOnlyDictionary<string, string?> parameters);
    Task<string?> GetExplanationOfBenefitJsonAsync(string claimId);
    Task<List<MassAdjudicationRunSummary>> GetMassAdjudicationRunsAsync(int limit = 25);
    Task<MassAdjudicationRunSummary?> GetMassAdjudicationRunAsync(string runId);
    Task<List<MassAdjudicationClaimResult>> GetMassAdjudicationClaimResultsAsync(
        string runId,
        string? outcome = null,
        int limit = 250,
        string? validationStatus = null,
        string? paymentStatus = null);
    Task<string> SubmitClaimAsync(SubmitClaimRequest request);
    Task<bool> TryRecordAiExaminerAgreementAsync(
        string claimId,
        string agreement,
        string examinerUserId,
        string? notes = null);
    Task<AdjudicationTransparencyData?> GetAdjudicationDataAsync(string claimId);

    /// <summary>
    /// Prior claims the duplicate-claim stage matched this claim against
    /// (claims-service <c>GET /claims/{id}/duplicate-matches</c>). Empty when
    /// the claim has no duplicate findings or no longer exists.
    /// </summary>
    Task<List<ClaimDuplicateMatch>> GetDuplicateMatchesAsync(string claimId);

    /// <summary>
    /// Search the claims-service v1 endpoint for a member; returns FHIR
    /// ExplanationOfBenefit resources wrapped with pagination metadata. Used
    /// by the portal Member Details dialog Claims tab; the dialog only shows
    /// counts and links out — the full grid stays on /claims.
    /// </summary>
    Task<EobSearchResponse> SearchClaimsByMemberAsync(string memberId, MemberClaimsFilter filter);

    /// <summary>The claim's notes, oldest first (claims-service <c>GET /claims/{id}/notes</c>).</summary>
    Task<List<ClaimNoteView>> GetClaimNotesAsync(string claimId);

    /// <summary>
    /// Append a note. Notes can't be edited or deleted; the author and time
    /// are set by claims-service from the caller's token.
    /// </summary>
    Task<ClaimNoteView> AddClaimNoteAsync(string claimId, string category, string text);

    /// <summary>
    /// The edits on a pended claim with the override policy for each and
    /// what the caller may do (claims-service <c>GET /claims/{id}/edits</c>).
    /// Null when the claim doesn't exist.
    /// </summary>
    Task<ClaimEditsView?> GetClaimEditsAsync(string claimId);

    /// <summary>
    /// Who read this claim, newest first: views, and searches that returned it
    /// (claims-service <c>GET /claims/{id}/access-log</c>, needs <c>audit:read</c>).
    /// Reading the log is itself recorded.
    /// </summary>
    Task<List<ClaimAccessEntry>> GetClaimAccessLogAsync(string claimId, int limit = 200);

    /// <summary>
    /// Claims the signed-in user opened recently, newest first, one per claim
    /// (claims-service <c>GET /claims/recently-viewed</c>, built from the
    /// caller's own access events).
    /// </summary>
    Task<List<RecentClaimView>> GetRecentlyViewedClaimsAsync(int limit = 10);
}
