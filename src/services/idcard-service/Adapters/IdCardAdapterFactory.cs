using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using IdCardService.Services;

namespace IdCardService.Adapters;

/// <summary>
/// Resolves the correct <see cref="IIdCardAdapter"/> at runtime based on
/// tenant configuration. Mirrors the pattern used by
/// <c>EligibilityAdapterFactory</c>. Defaults to "cho" when tenant-service
/// answers and the tenant configures no platform. A refusal from tenant-service
/// (401/403) is never read as "no configuration": it throws
/// <see cref="TenantPlatformUnavailableException"/> and nothing is cached.
/// </summary>
public class IdCardAdapterFactory
{
    /// <summary>Named IHttpClientFactory client for tenant-service.</summary>
    public const string HttpClientName = "TenantService";

    private readonly IEnumerable<IIdCardAdapter> _adapters;
    private readonly UpstreamAuthorization _upstream;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<IdCardAdapterFactory> _logger;

    private readonly ConcurrentDictionary<string, (string Platform, Dictionary<string, string> Settings, DateTime ExpiresAt)> _cache = new();
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

    public IdCardAdapterFactory(
        IEnumerable<IIdCardAdapter> adapters,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<IdCardAdapterFactory> logger,
        UpstreamAuthorization upstream)
    {
        _adapters = adapters;
        _upstream = upstream;
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

        HttpResponseMessage response;
        try
        {
            var tenantUrl = _configuration["Services:TenantService"]
                ?? "http://tenant-service.cloudhealthoffice/api/v1";
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"{tenantUrl}/tenants/{Uri.EscapeDataString(tenantId)}");
            // X-Tenant-ID lets the outbound handler mint a service token for this
            // tenant when there is no caller to forward (tenant-service requires a
            // CHO token and takes the tenant only from it).
            _upstream.Prepare(request, tenantId);
            response = await _httpClientFactory.CreateClient(HttpClientName).SendAsync(request, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // tenant-service unreachable: use the default for this call only, so the
            // next order asks again instead of reusing a guess for five minutes.
            _logger.LogWarning(ex, "Failed to fetch tenant id-card config for {TenantId}; using default 'cho' (not cached)",
                Sanitize(tenantId));
            return ("cho", new Dictionary<string, string>());
        }

        using (response)
        {
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                // A refusal means this service cannot read the tenant's
                // configuration. Issuing on the default platform would quietly
                // bypass a QNXT or vendor tenant's setup, so the order fails.
                _logger.LogError(
                    "tenant-service refused the id-card platform lookup for {TenantId} with {Status}; " +
                    "the platform cannot be determined and no default is used",
                    Sanitize(tenantId), (int)response.StatusCode);
                throw new TenantPlatformUnavailableException(tenantId, response.StatusCode);
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "tenant-service responded {Status} for tenant {TenantId}; using default 'cho' (not cached)",
                    (int)response.StatusCode, Sanitize(tenantId));
                return ("cho", new Dictionary<string, string>());
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Two shapes are accepted for the platform selector — the
            // canonical `configuration.idCardPlatform.*` block (added
            // when QNXT or vendor onboarding requires it) and the
            // pass-through `configuration.customSettings.idCardPlatform`
            // key supported by the current tenant-service schema. When
            // neither is present we default to "cho" below.
            if (root.TryGetProperty("configuration", out var config) && config.ValueKind == JsonValueKind.Object)
            {
                if (config.TryGetProperty("idCardPlatform", out var idcConfig) &&
                    idcConfig.ValueKind == JsonValueKind.Object &&
                    idcConfig.TryGetProperty("platform", out var platformProp))
                {
                    var platform = platformProp.GetString() ?? "cho";
                    var settings = new Dictionary<string, string>();

                    if (idcConfig.TryGetProperty("platformSettings", out var settingsProp) &&
                        settingsProp.ValueKind == JsonValueKind.Object)
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
                    customSettings.ValueKind == JsonValueKind.Object &&
                    customSettings.TryGetProperty("idCardPlatform", out var customPlatform))
                {
                    var platform = customPlatform.GetString() ?? "cho";
                    var settings = new Dictionary<string, string>();
                    _cache[tenantId] = (platform, settings, DateTime.UtcNow.Add(CacheDuration));
                    return (platform, settings);
                }
            }
        }

        // tenant-service answered and the tenant configures no platform: "cho".
        var defaults = new Dictionary<string, string>();
        _cache[tenantId] = ("cho", defaults, DateTime.UtcNow.Add(CacheDuration));
        return ("cho", defaults);
    }

    private static string Sanitize(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", "").Replace("\n", "");
}

/// <summary>
/// tenant-service refused the ID card platform lookup, so the tenant's platform
/// is unknown. Orders fail instead of being issued on a guessed platform.
/// </summary>
public sealed class TenantPlatformUnavailableException : Exception
{
    public TenantPlatformUnavailableException(string tenantId, HttpStatusCode status)
        : base($"tenant-service refused the ID card platform lookup ({(int)status}).")
    {
        TenantId = tenantId;
        Status = status;
    }

    public string TenantId { get; }

    public HttpStatusCode Status { get; }
}
