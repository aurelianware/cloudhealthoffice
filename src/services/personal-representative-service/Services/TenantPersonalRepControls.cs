using System.Collections.Concurrent;
using System.Text.Json;
using CloudHealthOffice.Infrastructure.Logging;
using CloudHealthOffice.PlanConfiguration;

namespace PersonalRepresentativeService.Services;

/// <summary>
/// Per-tenant personal-representative controls, resolved through plan
/// configuration (<c>personalRepresentative.requireSecondUserActivation</c>,
/// plan-configuration.md §12.5).
/// </summary>
public interface ITenantPersonalRepControls
{
    /// <summary>
    /// Whether activating a representative needs a user other than the one who
    /// created it. True unless the tenant has an explicit <c>false</c> in plan
    /// configuration — or, until that value is moved, in the tenant document's
    /// <c>configuration.personalRepresentativeControls.requireSecondPerson</c>.
    /// </summary>
    Task<bool> IsSecondPersonRequiredAsync(string tenantId, CancellationToken ct = default);
}

/// <summary>
/// Resolves the setting through <see cref="IPlanSettings"/>, the same way
/// capitation-service resolves <c>payment.enforceSeparationOfDuties</c>: the
/// client caches, listens for configuration changes, and answers the
/// definition's default (<c>true</c>) when tenant-service cannot be reached.
///
/// <para>
/// Where nobody has set the setting it still consults the tenant document, so a
/// tenant that turned the second person off before plan configuration existed
/// does not have it switched back on without being told. That read is logged as
/// a warning naming the tenant and goes away once those values are moved.
/// </para>
/// </summary>
public sealed class TenantPersonalRepControls : ITenantPersonalRepControls
{
    public const string HttpClientName = "TenantService";

    /// <summary>The catalog setting this control resolves.</summary>
    public static readonly string SettingKey = PlanSettings.PersonalRepresentativeRequireSecondUserActivation.Key;

    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

    private readonly IPlanSettings _settings;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<TenantPersonalRepControls> _logger;
    private readonly ConcurrentDictionary<string, (bool Required, DateTime ExpiresAt)> _legacyCache = new();

    public TenantPersonalRepControls(
        IPlanSettings settings,
        IHttpClientFactory httpClientFactory,
        ILogger<TenantPersonalRepControls> logger)
    {
        _settings = settings;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<bool> IsSecondPersonRequiredAsync(string tenantId, CancellationToken ct = default)
    {
        var resolved = await _settings.ResolveAsync(tenantId, SettingKey, SettingScope.Empty, ct: ct);

        if (resolved.IsFallback)
        {
            _logger.LogWarning(
                "Could not resolve {Key} for tenant {TenantId}; personal representative activation still needs a second person",
                SettingKey, LogSanitizer.Sanitize(tenantId, 256));
            return true;
        }

        if (resolved.SourceLevel != SettingLevel.Platform) return resolved.AsBool();

        return await LegacyRequiredAsync(tenantId, ct);
    }

    /// <summary>
    /// The pre-plan-configuration home of this control. A second person is
    /// required unless the tenant document says an explicit <c>false</c>; a read
    /// that fails leaves one required.
    /// </summary>
    private async Task<bool> LegacyRequiredAsync(string tenantId, CancellationToken ct)
    {
        if (_legacyCache.TryGetValue(tenantId, out var cached) && cached.ExpiresAt > DateTime.UtcNow)
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
                    (int)response.StatusCode, LogSanitizer.Sanitize(tenantId, 256));
                return true; // not cached: a transient failure must not pin the answer
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            var required = ParseRequired(json);
            if (!required)
                _logger.LogWarning(
                    "Tenant {TenantId} turns the second person off in its tenant document. "
                    + "Move the value to the {Key} setting in plan configuration; this read is going away.",
                    LogSanitizer.Sanitize(tenantId, 256), SettingKey);
            _legacyCache[tenantId] = (required, DateTime.UtcNow.Add(CacheDuration));
            return required;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex,
                "Could not read personal representative controls for tenant {TenantId}; activation still needs a second person",
                LogSanitizer.Sanitize(tenantId, 256));
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

    /// <summary>Test seam: drops all cached entries of the tenant-document read.</summary>
    public void Clear() => _legacyCache.Clear();
}
