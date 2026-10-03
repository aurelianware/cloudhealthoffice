using System.Net;
using System.Text.Json;
using CloudHealthOffice.Infrastructure.Middleware;
using Microsoft.Extensions.Logging;

namespace CloudHealthOffice.Infrastructure.Tenancy;

/// <summary>What tenant-service said about a tenant's platform.</summary>
public enum TenantPlatformOutcome
{
    /// <summary>tenant-service answered 2xx and the tenant names a platform. Cached.</summary>
    Configured,

    /// <summary>tenant-service answered 2xx and the tenant names no platform: the default applies. Cached.</summary>
    NotConfigured,

    /// <summary>
    /// No usable answer: tenant-service was unreachable, timed out, answered
    /// 404, 5xx or another non-success status, or sent a body that could not
    /// be read. The default applies to this call only and is never cached, so
    /// the next call asks again.
    /// </summary>
    Unavailable,

    /// <summary>
    /// tenant-service answered 401 or 403: this service is not authenticated or
    /// not authorized there. The platform is unknown and the operation must
    /// fail. Never cached and never answered with the default.
    /// </summary>
    Refused,
}

/// <summary>Result of <see cref="TenantPlatformLookup.FetchAsync"/>.</summary>
public sealed record TenantPlatformLookupResult(
    TenantPlatformOutcome Outcome,
    string Platform,
    Dictionary<string, string> Settings,
    HttpStatusCode? StatusCode = null)
{
    /// <summary>Only an answer tenant-service actually gave may be cached.</summary>
    public bool IsCacheable => Outcome is TenantPlatformOutcome.Configured or TenantPlatformOutcome.NotConfigured;
}

/// <summary>
/// The one rule CHO services use to read a tenant's <c>configuration.&lt;key&gt;Platform</c>
/// block from tenant-service (claims, benefit-plan, provider, eligibility,
/// id-card).
///
/// <list type="bullet">
///   <item>2xx with a platform: that platform. Cacheable.</item>
///   <item>2xx without one: the default (<c>cho</c>). Cacheable.</item>
///   <item>401/403: <see cref="TenantPlatformOutcome.Refused"/>, logged as an
///   error. Callers throw; nothing is cached.</item>
///   <item>Anything else (404, 5xx, other status, transport failure, timeout,
///   unreadable body): the default for this call only, logged as a warning,
///   never cached.</item>
/// </list>
///
/// The request names the tenant in <c>X-Tenant-ID</c>, so the shared outbound
/// token handler forwards the caller's token or, with no caller, mints a
/// service token for that tenant. The client must come from
/// <see cref="IHttpClientFactory"/> for that handler to run.
/// </summary>
public static class TenantPlatformLookup
{
    public const string DefaultPlatform = "cho";
    public const string DefaultTenantServiceUrl = "http://tenant-service.cloudhealthoffice/api/v1";

    /// <summary>
    /// Reads <c>configuration.{platformKey}</c> (<c>platform</c> and
    /// <c>platformSettings</c>) for <paramref name="tenantId"/>.
    /// <paramref name="alternateParser"/> may read another shape from the
    /// <c>configuration</c> object when the canonical block is absent.
    /// <paramref name="prepare"/> runs on the request after <c>X-Tenant-ID</c>
    /// is set (for a service that attaches its own credentials).
    /// </summary>
    public static async Task<TenantPlatformLookupResult> FetchAsync(
        HttpClient client,
        string? tenantServiceBaseUrl,
        string tenantId,
        string platformKey,
        ILogger logger,
        CancellationToken ct = default,
        Func<JsonElement, (string Platform, Dictionary<string, string> Settings)?>? alternateParser = null,
        Action<HttpRequestMessage>? prepare = null)
    {
        var baseUrl = string.IsNullOrWhiteSpace(tenantServiceBaseUrl) ? DefaultTenantServiceUrl : tenantServiceBaseUrl;
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get, $"{baseUrl.TrimEnd('/')}/tenants/{Uri.EscapeDataString(tenantId)}");
            request.Headers.Add(TenantMiddleware.TenantHeaderName, tenantId);
            prepare?.Invoke(request);
            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                logger.LogError(
                    "tenant-service refused the {PlatformKey} lookup for {TenantId} with {StatusCode}; this service is " +
                    "not authenticated or not authorized there. The platform is unknown and no default is used.",
                    platformKey, Sanitize(tenantId), (int)response.StatusCode);
                return new(TenantPlatformOutcome.Refused, DefaultPlatform, new(), response.StatusCode);
            }

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "tenant-service answered {StatusCode} to the {PlatformKey} lookup for {TenantId}; using '{Default}' " +
                    "for this call only (not cached)",
                    (int)response.StatusCode, platformKey, Sanitize(tenantId), DefaultPlatform);
                return new(TenantPlatformOutcome.Unavailable, DefaultPlatform, new(), response.StatusCode);
            }

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var parsed = Parse(doc.RootElement, platformKey, alternateParser);
            return parsed is { } found
                ? new(TenantPlatformOutcome.Configured, found.Platform, found.Settings, response.StatusCode)
                : new(TenantPlatformOutcome.NotConfigured, DefaultPlatform, new(), response.StatusCode);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Could not read the {PlatformKey} configuration for {TenantId} from tenant-service; using '{Default}' " +
                "for this call only (not cached)",
                platformKey, Sanitize(tenantId), DefaultPlatform);
            return new(TenantPlatformOutcome.Unavailable, DefaultPlatform, new());
        }
    }

    private static (string Platform, Dictionary<string, string> Settings)? Parse(
        JsonElement root,
        string platformKey,
        Func<JsonElement, (string Platform, Dictionary<string, string> Settings)?>? alternateParser)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("configuration", out var config)
            || config.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (config.TryGetProperty(platformKey, out var block)
            && block.ValueKind == JsonValueKind.Object
            && block.TryGetProperty("platform", out var platformProp)
            && platformProp.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(platformProp.GetString()))
        {
            var settings = new Dictionary<string, string>();
            if (block.TryGetProperty("platformSettings", out var settingsProp)
                && settingsProp.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in settingsProp.EnumerateObject())
                    settings[prop.Name] = prop.Value.ValueKind == JsonValueKind.String
                        ? prop.Value.GetString() ?? string.Empty
                        : prop.Value.GetRawText();
            }

            return (platformProp.GetString()!, settings);
        }

        return alternateParser?.Invoke(config);
    }

    private static string Sanitize(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", string.Empty).Replace("\n", string.Empty);
}
