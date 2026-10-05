using System.Collections.Concurrent;
using System.Text.Json;

namespace PersonalRepresentativeService.Services;

/// <summary>
/// Per-tenant personal-representative controls, read from the tenant's
/// configuration in tenant-service (<c>configuration.personalRepresentativeControls</c>).
/// </summary>
public interface ITenantPersonalRepControls
{
    /// <summary>
    /// Whether activating a representative needs a user other than the one who
    /// created it. True unless the tenant explicitly sets
    /// <c>configuration.personalRepresentativeControls.requireSecondPerson</c> to <c>false</c>.
    /// </summary>
    Task<bool> IsSecondPersonRequiredAsync(string tenantId, CancellationToken ct = default);
}

/// <summary>
/// Reads the tenant document from tenant-service the way capitation-service reads
/// <c>paymentControls</c> (5-minute cache per tenant, successful reads only).
/// Fails closed: a missing tenant, missing setting, unreadable response or
/// unreachable tenant-service all mean a second person is required. Only an
/// explicit JSON <c>false</c> turns it off.
/// </summary>
public sealed class TenantPersonalRepControls : ITenantPersonalRepControls
{
    public const string HttpClientName = "TenantService";

    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<TenantPersonalRepControls> _logger;
    private readonly ConcurrentDictionary<string, (bool Required, DateTime ExpiresAt)> _cache = new();

    public TenantPersonalRepControls(IHttpClientFactory httpClientFactory, ILogger<TenantPersonalRepControls> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<bool> IsSecondPersonRequiredAsync(string tenantId, CancellationToken ct = default)
    {
        if (_cache.TryGetValue(tenantId, out var cached) && cached.ExpiresAt > DateTime.UtcNow)
            return cached.Required;

        try
        {
            var client = _httpClientFactory.CreateClient(HttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Get, $"api/v1/tenants/{Uri.EscapeDataString(tenantId)}");
            request.Headers.Add("X-Tenant-ID", tenantId);
            using var response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "tenant-service returned {Status} for tenant {TenantId}; personal representative activation still needs a second person",
                    (int)response.StatusCode, LogSanitizer.SafeForLog(tenantId));
                return true; // not cached: a transient failure must not pin the answer
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            var required = ParseRequired(json);
            _cache[tenantId] = (required, DateTime.UtcNow.Add(CacheDuration));
            return required;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex,
                "Could not read personal representative controls for tenant {TenantId}; activation still needs a second person",
                LogSanitizer.SafeForLog(tenantId));
            return true;
        }
    }

    /// <summary>
    /// <c>false</c> only for an explicit
    /// <c>configuration.personalRepresentativeControls.requireSecondPerson: false</c>.
    /// </summary>
    public static bool ParseRequired(string tenantJson)
    {
        using var doc = JsonDocument.Parse(tenantJson);
        return !(doc.RootElement.ValueKind == JsonValueKind.Object
                 && doc.RootElement.TryGetProperty("configuration", out var configuration)
                 && configuration.ValueKind == JsonValueKind.Object
                 && configuration.TryGetProperty("personalRepresentativeControls", out var controls)
                 && controls.ValueKind == JsonValueKind.Object
                 && controls.TryGetProperty("requireSecondPerson", out var required)
                 && required.ValueKind == JsonValueKind.False);
    }

    /// <summary>Test seam: drops all cached entries.</summary>
    public void Clear() => _cache.Clear();
}
