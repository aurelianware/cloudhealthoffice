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
/// Follows the same rule as the tenant platform lookups
/// (<see cref="CloudHealthOffice.Infrastructure.Tenancy.TenantPlatformLookup"/>):
/// <list type="bullet">
///   <item>2xx: the tenant's configuration (or the default when the body is
///   empty). Cached.</item>
///   <item>401/403: benefit-plan-service is not trusted by tenant-service. Logged
///   as an error, not cached, and raised as
///   <see cref="OperatingModeUnavailableException"/>: adjudicating an Augment
///   tenant's claim as if CHO were authoritative would be silently wrong.</item>
///   <item>404, 5xx, transport failure, timeout or unreadable body: the default
///   (all engines in Replace mode) for this call only, not cached.</item>
/// </list>
/// The request names the tenant in <c>X-Tenant-ID</c>, so the shared outbound
/// token handler can mint a service token for it when there is no caller.
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

        HttpStatusCode refusal;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"api/v1/tenants/{Uri.EscapeDataString(tenantId)}/operating-mode");
            request.Headers.Add("X-Tenant-ID", tenantId);
            using var response = await _httpClient.SendAsync(request, ct);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                refusal = response.StatusCode;
            }
            else if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "tenant-service answered {StatusCode} for the operating mode of tenant {TenantId}; " +
                    "defaulting to Replace for all engines for this call only (not cached)",
                    (int)response.StatusCode, SanitizeForLog(tenantId));
                return new OperatingModeConfiguration { TenantId = tenantId };
            }
            else
            {
                var config = await response.Content.ReadFromJsonAsync<OperatingModeConfiguration>(ct);
                config ??= new OperatingModeConfiguration { TenantId = tenantId };

                _cache.Set(cacheKey, config, CacheTtl);
                return config;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to fetch operating mode for tenant {TenantId}; defaulting to Replace for all engines " +
                "for this call only (not cached)",
                SanitizeForLog(tenantId));
            return new OperatingModeConfiguration { TenantId = tenantId };
        }

        _logger.LogError(
            "tenant-service refused the operating mode lookup for tenant {TenantId} with {StatusCode}; " +
            "benefit-plan-service is not authenticated or not authorized there. No default is used.",
            SanitizeForLog(tenantId), (int)refusal);
        throw new OperatingModeUnavailableException(tenantId, refusal);
    }

    private static string SanitizeForLog(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", string.Empty).Replace("\n", string.Empty);
}

/// <summary>
/// tenant-service refused the operating mode lookup (401/403), so the tenant's
/// operating mode is unknown. Never answered with the default.
/// </summary>
public sealed class OperatingModeUnavailableException : InvalidOperationException
{
    public OperatingModeUnavailableException(string tenantId, HttpStatusCode statusCode)
        : base($"tenant-service refused the operating mode lookup ({(int)statusCode}).")
    {
        TenantId = tenantId;
        StatusCode = statusCode;
    }

    public string TenantId { get; }

    public HttpStatusCode StatusCode { get; }
}
