using ProviderService.Models;

namespace ProviderService.Adapters;

/// <summary>
/// Abstraction for provider directory platforms.
/// Each tenant can be configured to use a different adapter (CHO, QNXT, Facets, HealthEdge, ...).
/// The adapter normalizes platform-specific responses into a common, vendor-neutral format
/// designed to project cleanly onto future FHIR <c>Practitioner</c> / <c>Organization</c>
/// resources (Sections 5.7–5.9).
/// </summary>
/// <remarks>
/// Mirrors <c>BenefitPlanService.Adapters.IBenefitPlanAdapter</c> and
/// <c>EligibilityService.Adapters.IEligibilityAdapter</c>. The selection mechanism
/// (factory consults tenant-service config and falls back to "cho") is identical.
///
/// <para>
/// <see cref="GetNetworkAsync"/> is a deliberate placeholder — the Network entity
/// itself ships in capability 5.3. Until then every adapter throws
/// <see cref="NotImplementedException"/> from this method so callers fail loudly
/// rather than silently degrading.
/// </para>
/// </remarks>
public interface IProviderAdapter
{
    /// <summary>
    /// Platform identifier matching <c>ProviderConfig.Platform</c> on the tenant.
    /// Resolution by the factory is case-insensitive.
    /// </summary>
    string Platform { get; }

    /// <summary>
    /// Fetch a single provider by chain key (<see cref="ProviderAdapterRequest.ProviderId"/>).
    /// Returns a response with <c>Provider == null</c> when not found so callers can map to 404.
    /// </summary>
    Task<ProviderAdapterResponse> GetProviderAsync(
        ProviderAdapterRequest request, CancellationToken ct = default);

    /// <summary>
    /// Fetch a single provider by NPI (<see cref="ProviderAdapterRequest.Npi"/>).
    /// Returns a response with <c>Provider == null</c> when not found.
    /// </summary>
    Task<ProviderAdapterResponse> GetProviderByNpiAsync(
        ProviderAdapterRequest request, CancellationToken ct = default);

    /// <summary>
    /// Placeholder for capability 5.3 (Network entity). Throws
    /// <see cref="NotImplementedException"/> on every adapter today; the Network
    /// shape and storage land with 5.3.
    /// </summary>
    Task<NetworkAdapterResponse> GetNetworkAsync(
        ProviderAdapterRequest request, CancellationToken ct = default);

    /// <summary>
    /// Return providers participating in a given network / plan / line-of-business
    /// scope as derived from <see cref="ProviderAdapterRequest"/>. Implementations
    /// filter on <c>NetworkParticipation</c> for CHO; vendor adapters call the
    /// vendor's roster surface.
    /// </summary>
    Task<ProviderRosterAdapterResponse> GetNetworkRosterAsync(
        ProviderAdapterRequest request, CancellationToken ct = default);

    /// <summary>
    /// General provider search (name / specialty / location / type filters).
    /// Returns the page described by <see cref="ProviderAdapterRequest.Page"/> /
    /// <see cref="ProviderAdapterRequest.PageSize"/>.
    /// </summary>
    Task<ProviderRosterAdapterResponse> SearchProvidersAsync(
        ProviderAdapterRequest request, CancellationToken ct = default);
}
