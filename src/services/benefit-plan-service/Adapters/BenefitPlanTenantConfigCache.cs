using System.Collections.Concurrent;
using System.Net;
using CloudHealthOffice.Infrastructure.Tenancy;

namespace BenefitPlanService.Adapters;

/// <summary>
/// Singleton cache for per-tenant benefit-plan platform configuration. Holds
/// state across requests so the factory itself can stay scoped (the CHO
/// adapter wraps scoped business services, so the factory and adapters must
/// be scoped — but the cache must outlive a single request).
/// </summary>
/// <remarks>
/// 5-minute TTL, thread-safe via <see cref="ConcurrentDictionary{TKey,TValue}"/>.
/// The lookup follows <see cref="TenantPlatformLookup"/>, the rule shared with
/// claims, provider, eligibility and id-card:
/// <list type="bullet">
///   <item>tenant-service answers and names a platform, or names none
///   (default <c>"cho"</c>): cached.</item>
///   <item>401/403: logged as an error, not cached, and raised as
///   <see cref="BenefitPlanTenantConfigUnavailableException"/>, so a QNXT or
///   Facets tenant's plans are never silently read from CHO.</item>
///   <item>404, 5xx, transport failure or unreadable body: <c>"cho"</c> for this
///   call only, not cached.</item>
/// </list>
/// The request goes through an <see cref="IHttpClientFactory"/> client and
/// names the tenant in <c>X-Tenant-ID</c>, so the shared outbound handler can
/// mint a service token for that tenant when there is no caller.
/// </remarks>
public class BenefitPlanTenantConfigCache
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<BenefitPlanTenantConfigCache> _logger;

    private readonly ConcurrentDictionary<string, (string Platform, Dictionary<string, string> Settings, DateTime ExpiresAt)> _cache = new();
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

    public const string DefaultPlatform = TenantPlatformLookup.DefaultPlatform;
    public const string HttpClientName = "BenefitPlanDefault";
    public const string PlatformKey = "benefitPlanPlatform";

    public BenefitPlanTenantConfigCache(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<BenefitPlanTenantConfigCache> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// Resolve <c>(platform, platformSettings)</c> for the given tenant, hitting
    /// tenant-service on cache miss. Throws
    /// <see cref="BenefitPlanTenantConfigUnavailableException"/> when
    /// tenant-service refuses the lookup (401/403).
    /// </summary>
    public async Task<(string Platform, Dictionary<string, string> Settings)> GetAsync(
        string tenantId, CancellationToken ct = default)
    {
        if (_cache.TryGetValue(tenantId, out var cached) && cached.ExpiresAt > DateTime.UtcNow)
        {
            return (cached.Platform, cached.Settings);
        }

        var result = await TenantPlatformLookup.FetchAsync(
            _httpClientFactory.CreateClient(HttpClientName), _configuration["Services:TenantService"],
            tenantId, PlatformKey, _logger, ct);

        if (result.Outcome == TenantPlatformOutcome.Refused)
            throw new BenefitPlanTenantConfigUnavailableException(tenantId, result.StatusCode!.Value);

        if (result.IsCacheable)
            _cache[tenantId] = (result.Platform, result.Settings, DateTime.UtcNow.Add(CacheDuration));

        return (result.Platform, result.Settings);
    }

    /// <summary>Test seam — drops all cached entries.</summary>
    public void Clear() => _cache.Clear();
}

/// <summary>
/// tenant-service refused benefit-plan-service's platform lookup (401/403), so
/// the tenant's plan platform is unknown. Never answered with the default.
/// </summary>
public sealed class BenefitPlanTenantConfigUnavailableException : InvalidOperationException
{
    public BenefitPlanTenantConfigUnavailableException(string tenantId, HttpStatusCode statusCode)
        : base($"tenant-service refused the benefit-plan platform lookup ({(int)statusCode}).")
    {
        TenantId = tenantId;
        StatusCode = statusCode;
    }

    public string TenantId { get; }

    public HttpStatusCode StatusCode { get; }
}
