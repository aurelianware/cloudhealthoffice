using System.Collections.Concurrent;
using System.Text.Json;

namespace IdCardService.Adapters;

/// <summary>
/// Resolves the correct <see cref="IIdCardAdapter"/> at runtime based on
/// tenant configuration. Mirrors the pattern used by
/// <c>EligibilityAdapterFactory</c>. Defaults to "cho" when no configuration
/// is found.
/// </summary>
public class IdCardAdapterFactory
{
    private readonly IEnumerable<IIdCardAdapter> _adapters;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<IdCardAdapterFactory> _logger;

    private readonly ConcurrentDictionary<string, (string Platform, Dictionary<string, string> Settings, DateTime ExpiresAt)> _cache = new();
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

    public IdCardAdapterFactory(
        IEnumerable<IIdCardAdapter> adapters,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<IdCardAdapterFactory> logger)
    {
        _adapters = adapters;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<IIdCardAdapter> GetAdapterAsync(string tenantId, CancellationToken ct = default)
    {
        var (platform, _) = await GetTenantPlatformAsync(tenantId, ct);
        return Resolve(platform);
    }

    public async Task<(IIdCardAdapter Adapter, Dictionary<string, string> Settings)> GetAdapterWithSettingsAsync(
        string tenantId, CancellationToken ct = default)
    {
        var (platform, settings) = await GetTenantPlatformAsync(tenantId, ct);
        return (Resolve(platform), new Dictionary<string, string>(settings));
    }

    private IIdCardAdapter Resolve(string platform)
    {
        var adapter = _adapters.FirstOrDefault(a =>
            string.Equals(a.Platform, platform, StringComparison.OrdinalIgnoreCase));

        if (adapter == null)
        {
            _logger.LogWarning(
                "No id-card adapter found for platform '{Platform}', falling back to 'cho'", Sanitize(platform));
            adapter = _adapters.First(a =>
                string.Equals(a.Platform, "cho", StringComparison.OrdinalIgnoreCase));
        }

        return adapter;
    }

    private async Task<(string Platform, Dictionary<string, string> Settings)> GetTenantPlatformAsync(
        string tenantId, CancellationToken ct)
    {
        if (_cache.TryGetValue(tenantId, out var cached) && cached.ExpiresAt > DateTime.UtcNow)
        {
            return (cached.Platform, cached.Settings);
        }

        try
        {
            var tenantUrl = _configuration["Services:TenantService"]
                ?? "http://tenant-service.cloudhealthoffice/api/v1";
            var client = _httpClientFactory.CreateClient("IdCardDefault");
            var response = await client.GetAsync($"{tenantUrl}/tenants/{tenantId}", ct);

            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                // Two shapes are accepted for the platform selector — the
                // canonical `configuration.idCardPlatform.*` block (added
                // when QNXT or vendor onboarding requires it) and the
                // pass-through `configuration.customSettings.idCardPlatform`
                // key supported by the current tenant-service schema. When
                // neither is present we default to "cho" below.
                if (root.TryGetProperty("configuration", out var config))
                {
                    if (config.TryGetProperty("idCardPlatform", out var idcConfig) &&
                        idcConfig.TryGetProperty("platform", out var platformProp))
                    {
                        var platform = platformProp.GetString() ?? "cho";
                        var settings = new Dictionary<string, string>();

                        if (idcConfig.TryGetProperty("platformSettings", out var settingsProp))
                        {
                            foreach (var prop in settingsProp.EnumerateObject())
                            {
                                settings[prop.Name] = prop.Value.GetString() ?? string.Empty;
                            }
                        }

                        _cache[tenantId] = (platform, settings, DateTime.UtcNow.Add(CacheDuration));
                        return (platform, settings);
                    }

                    if (config.TryGetProperty("customSettings", out var customSettings) &&
                        customSettings.TryGetProperty("idCardPlatform", out var customPlatform))
                    {
                        var platform = customPlatform.GetString() ?? "cho";
                        var settings = new Dictionary<string, string>();
                        _cache[tenantId] = (platform, settings, DateTime.UtcNow.Add(CacheDuration));
                        return (platform, settings);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch tenant id-card config for {TenantId}; using default", Sanitize(tenantId));
        }

        var defaults = new Dictionary<string, string>();
        _cache[tenantId] = ("cho", defaults, DateTime.UtcNow.Add(CacheDuration));
        return ("cho", defaults);
    }

    private static string Sanitize(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", "").Replace("\n", "");
}
