using System.Collections.Concurrent;
using System.Text.Json;
using CloudHealthOffice.Infrastructure.Logging;
using CloudHealthOffice.PlanConfiguration;

namespace CapitationService.Services;

/// <summary>
/// Per-tenant payment controls, resolved through plan configuration
/// (<c>payment.enforceSeparationOfDuties</c>, plan-configuration.md §12.5).
/// </summary>
public interface ITenantPaymentControls
{
    /// <summary>
    /// Whether the tenant enforces payment separation of duties (maker-checker).
    /// True unless the tenant has an explicit <c>false</c> in plan configuration
    /// — or, until that value is moved, in the tenant document's
    /// <c>configuration.paymentControls.enforceSeparationOfDuties</c>.
    /// </summary>
    Task<bool> IsSeparationOfDutiesEnforcedAsync(string tenantId, CancellationToken ct = default);
}

/// <summary>
/// Resolves the setting through <see cref="IPlanSettings"/>, which caches,
/// listens for configuration changes and answers the definition's default
/// (<c>true</c>) when tenant-service cannot be reached. So this fails closed
/// the way it always did, without a cache or an HTTP call of its own.
///
/// <para>
/// Where nobody has set the setting it still consults the tenant document, so a
/// tenant that turned the rule off before plan configuration existed keeps the
/// behaviour it configured rather than silently having maker-checker switched
/// back on. That read is logged as a warning naming the tenant, and goes away
/// once those values are moved (plan-configuration.md §12.5).
/// </para>
/// </summary>
public sealed class TenantPaymentControls : ITenantPaymentControls
{
    public const string HttpClientName = "TenantService";

    /// <summary>The catalog setting this control resolves.</summary>
    public static readonly string SettingKey = PlanSettings.PaymentEnforceSeparationOfDuties.Key;

    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

    private readonly IPlanSettings _settings;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<TenantPaymentControls> _logger;
    private readonly ConcurrentDictionary<string, (bool Enforced, DateTime ExpiresAt)> _legacyCache = new();

    public TenantPaymentControls(
        IPlanSettings settings,
        IHttpClientFactory httpClientFactory,
        ILogger<TenantPaymentControls> logger)
    {
        _settings = settings;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<bool> IsSeparationOfDutiesEnforcedAsync(string tenantId, CancellationToken ct = default)
    {
        var resolved = await _settings.ResolveAsync(tenantId, SettingKey, SettingScope.Empty, ct: ct);

        // A fallback means tenant-service could not be asked at all. The answer
        // is the definition's default, which is the enforced one; reading the
        // tenant document instead would need the same unreachable service.
        if (resolved.IsFallback)
        {
            _logger.LogWarning(
                "Could not resolve {Key} for tenant {TenantId}; payment separation of duties stays enforced",
                SettingKey, LogSanitizer.Sanitize(tenantId));
            return true;
        }

        // Someone has set it: plan configuration is the authority.
        if (resolved.SourceLevel != SettingLevel.Platform) return resolved.AsBool();

        return await LegacyEnforcedAsync(tenantId, ct);
    }

    /// <summary>
    /// The pre-plan-configuration home of this control. Returns the setting's
    /// enforced default unless the tenant document says an explicit
    /// <c>false</c>; a read that fails leaves the rule enforced.
    /// </summary>
    private async Task<bool> LegacyEnforcedAsync(string tenantId, CancellationToken ct)
    {
        if (_legacyCache.TryGetValue(tenantId, out var cached) && cached.ExpiresAt > DateTime.UtcNow)
            return cached.Enforced;

        try
        {
            var client = _httpClientFactory.CreateClient(HttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Get, $"api/v1/tenants/{Uri.EscapeDataString(tenantId)}");
            // Names the tenant for ChoOutboundTokenHandler, so a lookup with no
            // inbound caller carries a service token for it.
            request.Headers.Add("X-Tenant-ID", tenantId);
            using var response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "tenant-service returned {Status} for tenant {TenantId}; payment separation of duties stays enforced",
                    (int)response.StatusCode, LogSanitizer.Sanitize(tenantId));
                return true; // not cached: a transient failure must not pin the answer
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            var enforced = ParseEnforced(json);
            if (!enforced)
                _logger.LogWarning(
                    "Tenant {TenantId} turns payment separation of duties off in its tenant document. "
                    + "Move the value to the {Key} setting in plan configuration; this read is going away.",
                    LogSanitizer.Sanitize(tenantId), SettingKey);
            _legacyCache[tenantId] = (enforced, DateTime.UtcNow.Add(CacheDuration));
            return enforced;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex,
                "Could not read payment controls for tenant {TenantId}; payment separation of duties stays enforced",
                LogSanitizer.Sanitize(tenantId));
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

    /// <summary>Test seam: drops all cached entries of the tenant-document read.</summary>
    public void Clear() => _legacyCache.Clear();
}
