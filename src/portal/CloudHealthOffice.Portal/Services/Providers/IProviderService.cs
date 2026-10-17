namespace CloudHealthOffice.Portal.Services;

public interface IProviderService
{
    Task<List<ProviderSummary>> SearchProvidersAsync(string searchTerm);
    Task<List<ProviderListItem>> SearchProvidersAsync(string? specialty = null, string? networkStatus = null, string? searchTerm = null);
    Task<ProviderDetails?> GetProviderByIdAsync(string providerId);
    Task<string> CreateProviderAsync(CreateProviderRequest request);
    Task UpdateProviderAsync(string providerId, UpdateProviderRequest request);
    Task<List<string>> GetSpecialtiesAsync();
    Task<ProviderNetworkInfo?> GetNetworkAsync(string networkId);
    Task<ProviderNetworkRoster?> GetNetworkRosterAsync(string networkId, DateTime asOfDate, int pageSize = 25);
    Task<ProviderNetworkMembership?> GetNetworkMembershipAsync(string networkId, string npi, DateTime asOfDate);

    /// <summary>
    /// Trigger an on-demand verification refresh for a single provider
    /// (capability 5.10). Wraps
    /// <c>POST /api/v1/providers/{id}/verification/refresh</c> on
    /// <c>provider-service</c>; the response carries the freshly
    /// projected integrity fields that the caller can splice back into
    /// the rendered detail view without round-tripping the full
    /// provider record.
    /// </summary>
    Task<ProviderIntegrityRefreshResult?> RefreshProviderVerificationAsync(string providerId);
}
