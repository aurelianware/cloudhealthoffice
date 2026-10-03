using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;

namespace ProviderService.Adapters;

/// <summary>
/// Singleton cache for per-tenant provider-directory platform configuration.
/// Holds state across requests so the factory itself can stay scoped (the CHO
/// adapter wraps scoped repository services, so the factory and adapters must
/// be scoped — but the cache must outlive a single request).
/// </summary>
/// <remarks>
/// Mirrors <c>BenefitPlanService.Adapters.BenefitPlanTenantConfigCache</c>:
/// 5-minute TTL, thread-safe via <see cref="ConcurrentDictionary{TKey,TValue}"/>.
/// <para>
/// tenant-service requires a CHO token. The request goes through an
/// <see cref="IHttpClientFactory"/> client and names the tenant in
/// <c>X-Tenant-ID</c>, so <c>ChoOutboundTokenHandler</c> forwards the
/// caller's token or, with no caller, mints a service token for that tenant.
/// </para>
/// <para>
/// A tenant with no <c>providerPlatform</c> block (or a 404) uses the
/// <c>"cho"</c> adapter, and a transport failure falls back to it. A 401 or
/// 403 is different: it means this service is not trusted by tenant-service,
/// and routing a QNXT/Facets tenant to the CHO directory would silently serve
/// the wrong data. It is logged as an error, never cached, and raised as
/// <see cref="ProviderTenantConfigUnavailableException"/>.
/// </para>
/// </remarks>
public class ProviderTenantConfigCache
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ProviderTenantConfigCache> _logger;

    private readonly ConcurrentDictionary<string, (string Platform, Dictionary<string, string> Settings, DateTime ExpiresAt)> _cache = new();
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

    public const string DefaultPlatform = "cho";
    public const string HttpClientName = "ProviderDefault";
    private const string TenantHeaderName = "X-Tenant-ID";

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
    /// tenant-service on cache miss. Defaults to <c>("cho", new())</c> when the
    /// tenant has no <c>providerPlatform</c> config or tenant-service cannot be
    /// reached. Throws <see cref="ProviderTenantConfigUnavailableException"/>
    /// when tenant-service answers 401/403.
    /// </summary>
    public async Task<(string Platform, Dictionary<string, string> Settings)> GetAsync(
        string tenantId, CancellationToken ct = default)
    {
        if (_cache.TryGetValue(tenantId, out var cached) && cached.ExpiresAt > DateTime.UtcNow)
        {
            return (cached.Platform, cached.Settings);
        }

        try
        {
            var tenantUrl = _configuration["Services:TenantService"]
                ?? "http://tenant-service.cloudhealthoffice/api/v1";
            var httpClient = _httpClientFactory.CreateClient(HttpClientName);
            // Encode the tenantId path segment defensively so an id with '/'
            // or '?' cannot alter the request path or query.
            var encodedTenantId = Uri.EscapeDataString(tenantId);
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{tenantUrl}/tenants/{encodedTenantId}");
            // Names the tenant for ChoOutboundTokenHandler: with no inbound
            // caller it mints a service token for this tenant.
            request.Headers.Add(TenantHeaderName, tenantId);
            using var response = await httpClient.SendAsync(request, ct);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                _logger.LogError(
                    "tenant-service refused the provider platform lookup for {TenantId} with {StatusCode}; " +
                    "provider-service is not authenticated or not authorized there. Not falling back to the default platform.",
                    SanitizeForLog(tenantId), (int)response.StatusCode);
                throw new ProviderTenantConfigUnavailableException(tenantId, response.StatusCode);
            }

            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.TryGetProperty("configuration", out var config) &&
                    config.TryGetProperty("providerPlatform", out var providerConfig) &&
                    providerConfig.TryGetProperty("platform", out var platformProp))
                {
                    var platform = platformProp.GetString() ?? DefaultPlatform;
                    var settings = new Dictionary<string, string>();

                    if (providerConfig.TryGetProperty("platformSettings", out var settingsProp))
                    {
                        foreach (var prop in settingsProp.EnumerateObject())
                        {
                            settings[prop.Name] = prop.Value.GetString() ?? string.Empty;
                        }
                    }

                    _cache[tenantId] = (platform, settings, DateTime.UtcNow.Add(CacheDuration));
                    return (platform, settings);
                }
            }
        }
        catch (ProviderTenantConfigUnavailableException)
        {
            throw;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to fetch provider tenant config for {TenantId}, using default adapter",
                SanitizeForLog(tenantId));
        }

        var defaultSettings = new Dictionary<string, string>();
        _cache[tenantId] = (DefaultPlatform, defaultSettings, DateTime.UtcNow.Add(CacheDuration));
        return (DefaultPlatform, defaultSettings);
    }

    /// <summary>Test seam — drops all cached entries.</summary>
    public void Clear() => _cache.Clear();

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
    }
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
