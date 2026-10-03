using System.Collections.Concurrent;
using System.Net;
using CloudHealthOffice.Infrastructure.Tenancy;

namespace ClaimsService.Adapters;

/// <summary>
/// Singleton cache for per-tenant claim platform configuration. Holds state
/// across requests so the factory itself can stay scoped (the CHO adapter
/// wraps the scoped <see cref="Repositories.IClaimRepository"/>, so the
/// factory and adapters must be scoped — but the cache must outlive a single
/// request).
/// </summary>
/// <remarks>
/// 5-minute TTL, thread-safe via <see cref="ConcurrentDictionary{TKey,TValue}"/>.
/// The lookup follows <see cref="TenantPlatformLookup"/>, the rule shared with
/// benefit-plan, provider, eligibility and id-card:
/// <list type="bullet">
///   <item>tenant-service answers and names a platform, or names none
///   (default <c>"cho"</c>): cached.</item>
///   <item>401/403: logged as an error, not cached, and raised as
///   <see cref="ClaimTenantConfigUnavailableException"/>. Routing a QNXT or
///   Facets tenant's claims to the CHO platform because claims-service is not
///   trusted by tenant-service would silently use the wrong system.</item>
///   <item>404, 5xx, transport failure or unreadable body: <c>"cho"</c> for this
///   call only, not cached.</item>
/// </list>
/// The request names the tenant in <c>X-Tenant-ID</c>, so from the
/// adjudication subscription (no HttpContext) the shared outbound handler mints
/// a service token for that tenant.
/// </remarks>
public class ClaimTenantConfigCache
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ClaimTenantConfigCache> _logger;

    private readonly ConcurrentDictionary<string, (string Platform, Dictionary<string, string> Settings, DateTime ExpiresAt)> _cache = new();
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

    public const string DefaultPlatform = TenantPlatformLookup.DefaultPlatform;
    public const string HttpClientName = "ClaimsDefault";
    public const string PlatformKey = "claimsPlatform";

    public ClaimTenantConfigCache(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<ClaimTenantConfigCache> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// Resolve <c>(platform, platformSettings)</c> for the given tenant,
    /// hitting tenant-service on cache miss. Throws
    /// <see cref="ClaimTenantConfigUnavailableException"/> when tenant-service
    /// refuses the lookup (401/403).
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
            throw new ClaimTenantConfigUnavailableException(tenantId, result.StatusCode!.Value);

        if (result.IsCacheable)
            _cache[tenantId] = (result.Platform, result.Settings, DateTime.UtcNow.Add(CacheDuration));

        return (result.Platform, result.Settings);
    }

    /// <summary>Test seam — drops all cached entries.</summary>
    public void Clear() => _cache.Clear();
}

/// <summary>
/// tenant-service refused claims-service's platform lookup (401/403), so the
/// tenant's claims platform is unknown. Never answered with the default.
/// </summary>
public sealed class ClaimTenantConfigUnavailableException : InvalidOperationException
{
    public ClaimTenantConfigUnavailableException(string tenantId, HttpStatusCode statusCode)
        : base($"tenant-service refused the claims platform lookup ({(int)statusCode}).")
    {
        TenantId = tenantId;
        StatusCode = statusCode;
    }

    public string TenantId { get; }

    public HttpStatusCode StatusCode { get; }
}
