using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using CloudHealthOffice.Infrastructure.Tenancy;
using IdCardService.Services;

namespace IdCardService.Adapters;

/// <summary>
/// Resolves the correct <see cref="IIdCardAdapter"/> at runtime based on
/// tenant configuration, following <see cref="TenantPlatformLookup"/> (the rule
/// shared with claims, benefit-plan, provider and eligibility). Defaults to
/// "cho" when tenant-service answers and the tenant configures no platform
/// (cached). A refusal from tenant-service (401/403) is never read as "no
/// configuration": it throws <see cref="TenantPlatformUnavailableException"/>
/// and nothing is cached. A 404, 5xx or unreachable tenant-service uses "cho"
/// for that order only, uncached.
/// </summary>
public class IdCardAdapterFactory
{
    /// <summary>Named IHttpClientFactory client for tenant-service.</summary>
    public const string HttpClientName = "TenantService";

    /// <summary>The tenant configuration block this factory reads.</summary>
    public const string PlatformKey = "idCardPlatform";

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

        // Two shapes are accepted for the platform selector: the canonical
        // `configuration.idCardPlatform` block (platform + platformSettings),
        // and the pass-through `configuration.customSettings.idCardPlatform`
        // string. X-Tenant-ID (set by the lookup, and again by Prepare) lets the
        // outbound handler mint a service token for this tenant when there is
        // no caller to forward.
        var result = await TenantPlatformLookup.FetchAsync(
            _httpClientFactory.CreateClient(HttpClientName),
            _configuration["Services:TenantService"],
            tenantId,
            PlatformKey,
            _logger,
            ct,
            alternateParser: ReadCustomSettingsPlatform,
            prepare: request => _upstream.Prepare(request, tenantId));

        if (result.Outcome == TenantPlatformOutcome.Refused)
        {
            // A refusal means this service cannot read the tenant's
            // configuration. Issuing on the default platform would quietly
            // bypass a QNXT or vendor tenant's setup, so the order fails.
            throw new TenantPlatformUnavailableException(tenantId, result.StatusCode!.Value);
        }

        // Only an answer tenant-service gave is cached; 404, 5xx or an
        // unreachable tenant-service use "cho" for this order only.
        if (result.IsCacheable)
            _cache[tenantId] = (result.Platform, result.Settings, DateTime.UtcNow.Add(CacheDuration));

        return (result.Platform, result.Settings);
    }

    private static (string Platform, Dictionary<string, string> Settings)? ReadCustomSettingsPlatform(JsonElement config)
    {
        if (config.TryGetProperty("customSettings", out var customSettings) &&
            customSettings.ValueKind == JsonValueKind.Object &&
            customSettings.TryGetProperty("idCardPlatform", out var customPlatform) &&
            customPlatform.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(customPlatform.GetString()))
        {
            return (customPlatform.GetString()!, new Dictionary<string, string>());
        }

        return null;
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
