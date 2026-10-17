namespace CloudHealthOffice.Portal.Services;

public interface IAuthorizationService
{
    Task<List<AuthorizationSummary>> GetAuthorizationsAsync(string? memberId = null);
    Task<AuthorizationDetails?> GetAuthorizationByIdAsync(string authorizationId);
    Task<string> SubmitAuthorizationAsync(SubmitAuthorizationRequest request);

    /// <summary>Authorization Insights: CMS-0057 figures for the period, the queue now, denial reasons.</summary>
    Task<AuthorizationInsightsView> GetAuthorizationInsightsAsync(DateTime from, DateTime to, string? lineOfBusiness = null);

    /// <summary>The authorizations behind one Insights figure; <paramref name="query"/> holds <see cref="AuthorizationDrillDown.Keys"/>.</summary>
    Task<List<AuthorizationSummary>> ListAuthorizationInsightAsync(IReadOnlyDictionary<string, string> query);
}
