namespace ProviderService.Models;

/// <summary>
/// Vendor-neutral request envelope passed to any
/// <see cref="Adapters.IProviderAdapter"/>. A single shape covers every read
/// method on the adapter; per-method required fields are documented on the
/// individual properties below.
/// </summary>
public class ProviderAdapterRequest
{
    /// <summary>Tenant id resolved by the request middleware. Required by all methods.</summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>
    /// Provider chain key (<see cref="Provider.ProviderId"/>). Required by
    /// <c>GetProviderAsync</c>; ignored otherwise.
    /// </summary>
    public string? ProviderId { get; set; }

    /// <summary>
    /// Specific version identifier (ULID). Optional on
    /// <c>GetProviderAsync</c> — when set, adapters return that exact version
    /// instead of the latest non-Draft head.
    /// </summary>
    public string? VersionId { get; set; }

    /// <summary>
    /// 10-digit National Provider Identifier. Required by <c>GetProviderByNpiAsync</c>;
    /// ignored otherwise.
    /// </summary>
    public string? Npi { get; set; }

    /// <summary>
    /// Network identifier. Required by <c>GetNetworkAsync</c> /
    /// <c>GetNetworkRosterAsync</c> once capability 5.3 ships; today the CHO
    /// adapter treats this as an optional roster scoping hint.
    /// </summary>
    public string? NetworkId { get; set; }

    /// <summary>Free-text name fragment used by <c>SearchProvidersAsync</c>.</summary>
    public string? Name { get; set; }

    /// <summary>NUCC taxonomy fragment (display name or code).</summary>
    public string? Specialty { get; set; }

    /// <summary>2-letter state code filter for search / roster.</summary>
    public string? State { get; set; }

    /// <summary>ZIP code filter for search / roster.</summary>
    public string? ZipCode { get; set; }

    /// <summary>Plan id used to scope <c>SearchProvidersAsync</c> / <c>GetNetworkRosterAsync</c>.</summary>
    public string? PlanId { get; set; }

    /// <summary>Line of business filter.</summary>
    public LineOfBusiness? LineOfBusiness { get; set; }

    /// <summary>Provider type (Individual / Organization) filter.</summary>
    public ProviderType? ProviderType { get; set; }

    /// <summary>Filter to providers accepting new patients.</summary>
    public bool? AcceptingNewPatients { get; set; }

    /// <summary>1-based page index for paged search / roster results.</summary>
    public int Page { get; set; } = 1;

    /// <summary>Page size for paged search / roster results.</summary>
    public int PageSize { get; set; } = 50;

    /// <summary>
    /// Effective date used to resolve which version applies on
    /// <c>GetNetworkRosterAsync</c> / network-status checks. Optional; when
    /// null, callers and adapter implementations should treat it as
    /// <see cref="DateTime.UtcNow"/> at call time.
    /// </summary>
    public DateTime? ServiceDate { get; set; }

    /// <summary>
    /// Platform-specific configuration sourced from
    /// <c>ProviderConfig.PlatformSettings</c> (e.g. QNXT base URL,
    /// Facets credential reference). Adapters read what they need; the
    /// factory passes the value through unchanged.
    /// </summary>
    public Dictionary<string, string> PlatformSettings { get; set; } = new();
}
