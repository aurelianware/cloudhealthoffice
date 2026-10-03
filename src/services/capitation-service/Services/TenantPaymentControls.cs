using System.Collections.Concurrent;
using System.Text.Json;

namespace CapitationService.Services;

/// <summary>
/// Per-tenant payment controls, read from the tenant's configuration in
/// tenant-service (<c>configuration.paymentControls</c>).
/// </summary>
public interface ITenantPaymentControls
{
    /// <summary>
    /// Whether the tenant enforces payment separation of duties (maker-checker).
    /// True unless the tenant explicitly sets
    /// <c>configuration.paymentControls.enforceSeparationOfDuties</c> to <c>false</c>.
    /// </summary>
    Task<bool> IsSeparationOfDutiesEnforcedAsync(string tenantId, CancellationToken ct = default);
}

/// <summary>
/// Reads the tenant document from tenant-service, as the other services' tenant
/// config caches do (5-minute cache per tenant). Fails closed: a missing tenant,
/// missing setting, unreadable response or unreachable tenant-service all mean
/// the rule is enforced. Only an explicit JSON <c>false</c> turns it off.
/// </summary>
public sealed class TenantPaymentControls : ITenantPaymentControls
{
    public const string HttpClientName = "TenantService";

    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<TenantPaymentControls> _logger;
    private readonly ConcurrentDictionary<string, (bool Enforced, DateTime ExpiresAt)> _cache = new();

    public TenantPaymentControls(IHttpClientFactory httpClientFactory, ILogger<TenantPaymentControls> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<bool> IsSeparationOfDutiesEnforcedAsync(string tenantId, CancellationToken ct = default)
    {
        if (_cache.TryGetValue(tenantId, out var cached) && cached.ExpiresAt > DateTime.UtcNow)
            return cached.Enforced;

        try
        {
            var client = _httpClientFactory.CreateClient(HttpClientName);
            using var response = await client.GetAsync($"api/v1/tenants/{Uri.EscapeDataString(tenantId)}", ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "tenant-service returned {Status} for tenant {TenantId}; payment separation of duties stays enforced",
                    (int)response.StatusCode, Sanitize(tenantId));
                return true; // not cached: a transient failure must not pin the answer
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            var enforced = ParseEnforced(json);
            _cache[tenantId] = (enforced, DateTime.UtcNow.Add(CacheDuration));
            return enforced;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex,
                "Could not read payment controls for tenant {TenantId}; payment separation of duties stays enforced",
                Sanitize(tenantId));
            return true;
        }
    }

    /// <summary>
    /// <c>false</c> only for an explicit <c>configuration.paymentControls.enforceSeparationOfDuties: false</c>.
    /// </summary>
    public static bool ParseEnforced(string tenantJson)
    {
        using var doc = JsonDocument.Parse(tenantJson);
        return !(doc.RootElement.ValueKind == JsonValueKind.Object
                 && doc.RootElement.TryGetProperty("configuration", out var configuration)
                 && configuration.ValueKind == JsonValueKind.Object
                 && configuration.TryGetProperty("paymentControls", out var controls)
                 && controls.ValueKind == JsonValueKind.Object
                 && controls.TryGetProperty("enforceSeparationOfDuties", out var enforce)
                 && enforce.ValueKind == JsonValueKind.False);
    }

    /// <summary>Test seam: drops all cached entries.</summary>
    public void Clear() => _cache.Clear();

    private static string Sanitize(string? value)
        => string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", string.Empty).Replace("\n", string.Empty);
}
