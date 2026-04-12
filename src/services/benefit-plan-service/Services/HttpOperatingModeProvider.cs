using System.Net;
using System.Net.Http.Json;
using CloudHealthOffice.OperatingMode;
using Microsoft.Extensions.Caching.Memory;

namespace BenefitPlanService.Services;

/// <summary>
/// Fetches tenant operating mode configuration from tenant-service with
/// in-memory caching. Cache TTL is 5 minutes — operating mode changes
/// are admin actions, not hot-path mutations.
///
/// Falls back to a default configuration (all engines in Replace mode)
/// when tenant-service is unreachable, so adjudication is never blocked
/// by a mode lookup failure. A 404 is treated as "no config" (default Replace),
/// not as an error.
/// </summary>
public class HttpOperatingModeProvider : IOperatingModeProvider
{
    private readonly HttpClient _httpClient;
    private readonly IMemoryCache _cache;
    private readonly ILogger<HttpOperatingModeProvider> _logger;

    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    public HttpOperatingModeProvider(
        HttpClient httpClient,
        IMemoryCache cache,
        ILogger<HttpOperatingModeProvider> logger)
    {
        _httpClient = httpClient;
        _cache = cache;
        _logger = logger;
    }

    public async Task<OperatingModeConfiguration> GetConfigurationAsync(
        string tenantId,
        CancellationToken ct = default)
    {
        var cacheKey = $"operating-mode:{tenantId}";

        if (_cache.TryGetValue<OperatingModeConfiguration>(cacheKey, out var cached) && cached is not null)
            return cached;

        try
        {
            var response = await _httpClient.GetAsync(
                $"api/v1/tenants/{Uri.EscapeDataString(tenantId)}/operating-mode", ct);

            // 404 = tenant has no operating mode configured → default (all Replace)
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                var noConfig = new OperatingModeConfiguration { TenantId = tenantId };
                _cache.Set(cacheKey, noConfig, CacheTtl);
                return noConfig;
            }

            response.EnsureSuccessStatusCode();

            var config = await response.Content.ReadFromJsonAsync<OperatingModeConfiguration>(ct);
            config ??= new OperatingModeConfiguration { TenantId = tenantId };

            _cache.Set(cacheKey, config, CacheTtl);
            return config;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex,
                "Failed to fetch operating mode for tenant {TenantId}; defaulting to Replace for all engines",
                tenantId);

            // Default: all engines in Replace mode (CHO authoritative)
            var fallback = new OperatingModeConfiguration { TenantId = tenantId };
            _cache.Set(cacheKey, fallback, TimeSpan.FromSeconds(30)); // short TTL on failure
            return fallback;
        }
    }
}
