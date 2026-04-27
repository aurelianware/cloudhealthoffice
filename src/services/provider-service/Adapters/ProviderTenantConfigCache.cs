using System.Collections.Concurrent;
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
/// 5-minute TTL, thread-safe via <see cref="ConcurrentDictionary{TKey,TValue}"/>,
/// and a graceful fallback to <c>"cho"</c> on any HTTP/JSON failure so a flaky
/// tenant-service never breaks provider reads.
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
    /// tenant has no <c>providerPlatform</c> config or the call fails.
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
            // Encode the tenantId path segment defensively — the value flows
            // from JWT/header via TenantMiddleware, and a crafted id with '/'
            // or '?' would otherwise alter the request path or query.
            var encodedTenantId = Uri.EscapeDataString(tenantId);
            var response = await httpClient.GetAsync($"{tenantUrl}/tenants/{encodedTenantId}", ct);

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
