using System.Collections.Concurrent;
using System.Net;
using CloudHealthOffice.Infrastructure.Tenancy;

namespace EligibilityService.Adapters;

/// <summary>
/// Resolves the correct IEligibilityAdapter at runtime based on tenant configuration.
/// Fetches the tenant's EligibilityConfig from the tenant-service and matches
/// the configured platform to a registered adapter.
///
/// The lookup follows <see cref="TenantPlatformLookup"/>, the rule shared with
/// claims, benefit-plan, provider and id-card: an answer from tenant-service
/// (a platform, or none, meaning "cho") is cached; a 401/403 is logged as an
/// error, not cached, and raised as
/// <see cref="EligibilityTenantConfigUnavailableException"/>; a 404, 5xx or
/// unreachable tenant-service uses "cho" for that call only, uncached.
/// </summary>
public class EligibilityAdapterFactory
{
    private readonly IEnumerable<IEligibilityAdapter> _adapters;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<EligibilityAdapterFactory> _logger;

    // Cache tenant platform config to avoid repeated HTTP calls.
    // Key: tenantId, Value: (platform, settings, expiry)
    private readonly ConcurrentDictionary<string, (string Platform, Dictionary<string, string> Settings, DateTime ExpiresAt)> _cache = new();
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

    public const string HttpClientName = "EligibilityDefault";
    public const string PlatformKey = "eligibilityPlatform";

    public EligibilityAdapterFactory(
        IEnumerable<IEligibilityAdapter> adapters,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<EligibilityAdapterFactory> logger)
    {
        _adapters = adapters;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// Get the eligibility adapter configured for the given tenant.
    /// Returns the CHO adapter if no specific configuration is found.
    /// </summary>
    public async Task<IEligibilityAdapter> GetAdapterAsync(string tenantId, CancellationToken ct = default)
    {
        var (platform, _) = await GetTenantPlatformAsync(tenantId, ct);
        return ResolveAdapter(platform);
    }

    /// <summary>
    /// Get the eligibility adapter and platform settings for the given tenant.
    /// </summary>
    public async Task<(IEligibilityAdapter Adapter, Dictionary<string, string> Settings)> GetAdapterWithSettingsAsync(
        string tenantId, CancellationToken ct = default)
    {
        var (platform, settings) = await GetTenantPlatformAsync(tenantId, ct);
        // Return a copy to prevent callers from mutating the cached dictionary
        return (ResolveAdapter(platform), new Dictionary<string, string>(settings));
    }

    private IEligibilityAdapter ResolveAdapter(string platform)
    {
        var adapter = _adapters.FirstOrDefault(a =>
            string.Equals(a.Platform, platform, StringComparison.OrdinalIgnoreCase));

        if (adapter == null)
        {
            _logger.LogWarning(
                "No eligibility adapter found for platform '{Platform}', falling back to 'cho'",
                platform);
            adapter = _adapters.First(a =>
                string.Equals(a.Platform, "cho", StringComparison.OrdinalIgnoreCase));
        }

        return adapter;
    }

    private async Task<(string Platform, Dictionary<string, string> Settings)> GetTenantPlatformAsync(
        string tenantId, CancellationToken ct)
    {
        // Check cache first
        if (_cache.TryGetValue(tenantId, out var cached) && cached.ExpiresAt > DateTime.UtcNow)
        {
            return (cached.Platform, cached.Settings);
        }

        // Naming the tenant (the lookup sets X-Tenant-ID) lets the outbound
        // token handler mint a service token when there is no caller (the
        // batch eligibility worker).
        var result = await TenantPlatformLookup.FetchAsync(
            _httpClientFactory.CreateClient(HttpClientName), _configuration["Services:TenantService"],
            tenantId, PlatformKey, _logger, ct);

        if (result.Outcome == TenantPlatformOutcome.Refused)
            throw new EligibilityTenantConfigUnavailableException(tenantId, result.StatusCode!.Value);

        if (result.IsCacheable)
            _cache[tenantId] = (result.Platform, result.Settings, DateTime.UtcNow.Add(CacheDuration));

        return (result.Platform, result.Settings);
    }
}

/// <summary>
/// tenant-service refused eligibility-service's platform lookup (401/403), so
/// the tenant's eligibility platform is unknown. Never answered with the default.
/// </summary>
public sealed class EligibilityTenantConfigUnavailableException : InvalidOperationException
{
    public EligibilityTenantConfigUnavailableException(string tenantId, HttpStatusCode statusCode)
        : base($"tenant-service refused the eligibility platform lookup ({(int)statusCode}).")
    {
        TenantId = tenantId;
        StatusCode = statusCode;
    }

    public string TenantId { get; }

    public HttpStatusCode StatusCode { get; }
}
