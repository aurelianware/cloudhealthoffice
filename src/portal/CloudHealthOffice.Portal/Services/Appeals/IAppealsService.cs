namespace CloudHealthOffice.Portal.Services;

// ---------------------------------------------------------------------------
// Appeals
// ---------------------------------------------------------------------------

public interface IAppealsService
{
    Task<AppealsSummary> GetSummaryAsync();
    Task<List<AppealSummary>> SearchAppealsAsync(string? appealId = null,
        string? memberId = null, string? originalClaimId = null);
    Task<AppealDetails?> GetAppealByIdAsync(string appealId);
}
