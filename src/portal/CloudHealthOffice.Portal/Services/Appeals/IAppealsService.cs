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

    /// <summary>Appeals &amp; Grievances Insights for the period, and the open queue now.</summary>
    Task<AppealInsightsView> GetAppealInsightsAsync(DateTime from, DateTime to, string? lineOfBusiness = null);

    /// <summary>The cases behind one Insights figure; <paramref name="query"/> holds <see cref="AppealDrillDown.Keys"/>.</summary>
    Task<List<AppealSummary>> ListAppealInsightAsync(IReadOnlyDictionary<string, string> query);
}
