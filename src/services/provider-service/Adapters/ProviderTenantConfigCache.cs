using System.Collections.Concurrent;
using System.Net;
using CloudHealthOffice.Infrastructure.Tenancy;

namespace ProviderService.Adapters;

/// <summary>
/// Singleton cache for per-tenant provider-directory platform configuration.
/// Holds state across requests so the factory itself can stay scoped (the CHO
/// adapter wraps scoped repository services, so the factory and adapters must
/// be scoped — but the cache must outlive a single request).
/// </summary>
/// <remarks>
/// 5-minute TTL, thread-safe via <see cref="ConcurrentDictionary{TKey,TValue}"/>.
/// <para>
/// tenant-service requires a CHO token. The request goes through an
/// <see cref="IHttpClientFactory"/> client and names the tenant in
/// <c>X-Tenant-ID</c>, so <c>ChoOutboundTokenHandler</c> forwards the
/// caller's token or, with no caller, mints a service token for that tenant.
/// </para>
/// <para>
/// The lookup follows <see cref="TenantPlatformLookup"/>, the rule shared with
/// claims, benefit-plan, eligibility and id-card. An answer from
/// tenant-service (a <c>providerPlatform</c> block, or none, meaning
/// <c>"cho"</c>) is cached. A 401 or 403 means this service is not trusted by
/// tenant-service, and routing a QNXT/Facets tenant to the CHO directory would
/// silently serve the wrong data: it is logged as an error, never cached, and
/// raised as <see cref="ProviderTenantConfigUnavailableException"/>. A 404,
/// 5xx, transport failure or unreadable body uses <c>"cho"</c> for that call
/// only and is not cached.
/// </para>
/// </remarks>
public class ProviderTenantConfigCache
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ProviderTenantConfigCache> _logger;

    private readonly ConcurrentDictionary<string, (string Platform, Dictionary<string, string> Settings, DateTime ExpiresAt)> _cache = new();
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

    public const string DefaultPlatform = TenantPlatformLookup.DefaultPlatform;
    public const string HttpClientName = "ProviderDefault";
    public const string PlatformKey = "providerPlatform";

    public ProviderTenantConfigCache(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<ProviderTenantConfigCache> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// Resolve <c>(platform, platformSettings)</c> for the given tenant, hitting
    /// tenant-service on cache miss. Throws
    /// <see cref="ProviderTenantConfigUnavailableException"/> when tenant-service
    /// answers 401/403.
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
            throw new ProviderTenantConfigUnavailableException(tenantId, result.StatusCode!.Value);

        if (result.IsCacheable)
            _cache[tenantId] = (result.Platform, result.Settings, DateTime.UtcNow.Add(CacheDuration));

        return (result.Platform, result.Settings);
    }

    /// <summary>Test seam — drops all cached entries.</summary>
    public void Clear() => _cache.Clear();
}

/// <summary>
/// tenant-service refused provider-service's platform lookup (401/403), so the
/// tenant's provider platform is unknown. Never answered with the default.
/// </summary>
public sealed class ProviderTenantConfigUnavailableException : InvalidOperationException
{
    public ProviderTenantConfigUnavailableException(string tenantId, HttpStatusCode statusCode)
        : base($"tenant-service refused the provider platform lookup ({(int)statusCode}).")
    {
        TenantId = tenantId;
        StatusCode = statusCode;
    }

    public string TenantId { get; }

    public HttpStatusCode StatusCode { get; }
}
