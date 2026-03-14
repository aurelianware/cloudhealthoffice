using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;

namespace EligibilityService.Adapters;

/// <summary>
/// Resolves the correct IEligibilityAdapter at runtime based on tenant configuration.
/// Fetches the tenant's EligibilityConfig from the tenant-service and matches
/// the configured platform to a registered adapter.
///
/// Defaults to "cho" (internal CHO services) when no configuration is found.
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

        try
        {
            var tenantUrl = _configuration["Services:TenantService"]
                ?? "http://tenant-service.cloudhealthoffice/api/v1";
            var httpClient = _httpClientFactory.CreateClient("EligibilityDefault");
            var response = await httpClient.GetAsync(
                $"{tenantUrl}/tenants/{tenantId}", ct);

            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.TryGetProperty("configuration", out var config) &&
                    config.TryGetProperty("eligibilityPlatform", out var eligConfig) &&
                    eligConfig.TryGetProperty("platform", out var platformProp))
                {
                    var platform = platformProp.GetString() ?? "cho";
                    var settings = new Dictionary<string, string>();

                    if (eligConfig.TryGetProperty("platformSettings", out var settingsProp))
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
            _logger.LogWarning(ex, "Failed to fetch tenant config for {TenantId}, using default adapter", SanitizeForLog(tenantId));
        }

        // Default to CHO
        var defaultSettings = new Dictionary<string, string>();
        _cache[tenantId] = ("cho", defaultSettings, DateTime.UtcNow.Add(CacheDuration));
        return ("cho", defaultSettings);
    }

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
    }
}
