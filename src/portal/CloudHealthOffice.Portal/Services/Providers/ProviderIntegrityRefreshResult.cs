namespace CloudHealthOffice.Portal.Services;

/// <summary>
/// Subset of <c>IntegrityProjectionRefreshResult</c> that the portal
/// renders after an on-demand refresh (capability 5.10). The shape
/// mirrors the four cached projection fields on
/// <c>Provider.IntegrityScore</c> in <c>provider-service</c>.
/// </summary>
public class ProviderIntegrityRefreshResult
{
    public string ProviderId { get; set; } = string.Empty;
    public int? IntegrityScore { get; set; }
    public string? IntegrityRating { get; set; }
    public DateTimeOffset? LastVerifiedAt { get; set; }
    public DateTimeOffset? NextVerificationDue { get; set; }
}
