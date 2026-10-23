using System.Collections.Concurrent;
using System.Text.Json;
using CloudHealthOffice.Infrastructure.Logging;

namespace WorkManagementService.Services;

/// <summary>What the directory established about one user's permission.</summary>
public enum Entitlement
{
    /// <summary>The user holds the permission.</summary>
    Granted,

    /// <summary>The user does not hold it, or is not a user of this tenant at all.</summary>
    Denied,

    /// <summary>No answer could be had: tenant-service was unreachable, errored, or replied with something unreadable.</summary>
    Unknown,
}

/// <summary>
/// Whether a user other than the caller holds a permission.
///
/// <para>The caller's own permissions come from their token and never need a
/// lookup. This is for the two places work management decides something about
/// somebody else: pushing an item to an assignee, and putting a member in a
/// queue.</para>
/// </summary>
public interface IWorkEntitlementDirectory
{
    Task<Entitlement> CheckAsync(string tenantId, string userId, string permission, CancellationToken ct = default);
}

/// <summary>
/// Reads <c>GET api/v1/tenants/{tenantId}/users/{userId}/permissions/{permission}</c>
/// from tenant-service, cached per (tenant, user, permission).
///
/// <para>A fresh answer is served from the cache for
/// <see cref="FreshFor"/>, so the steady state needs no call at all. When a
/// live call fails, an answer still within <see cref="StaleGraceFor"/> is
/// served rather than dropping to <see cref="Entitlement.Unknown"/>: a role
/// assignment that changed in the last hour is rarer than a momentary blip,
/// and serving last-known-good through a blip is what keeps the refusal below
/// rare enough to be worth having. Only with nothing cached at all is the
/// answer Unknown, and the callers refuse the action.</para>
///
/// <para>Failures are never cached as answers, so a blip cannot pin a user out
/// of their work for five minutes.</para>
/// </summary>
public sealed class WorkEntitlementDirectory : IWorkEntitlementDirectory
{
    public const string HttpClientName = "TenantService";

    /// <summary>How long a live answer is reused without asking again.</summary>
    public static readonly TimeSpan FreshFor = TimeSpan.FromMinutes(5);

    /// <summary>How long an answer may still be served once a live call has failed.</summary>
    public static readonly TimeSpan StaleGraceFor = TimeSpan.FromHours(1);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TimeProvider _time;
    private readonly ILogger<WorkEntitlementDirectory> _logger;
    private readonly ConcurrentDictionary<string, (bool Granted, DateTime At)> _cache = new(StringComparer.Ordinal);

    public WorkEntitlementDirectory(
        IHttpClientFactory httpClientFactory, TimeProvider time, ILogger<WorkEntitlementDirectory> logger)
    {
        _httpClientFactory = httpClientFactory;
        _time = time;
        _logger = logger;
    }

    public async Task<Entitlement> CheckAsync(string tenantId, string userId, string permission, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(permission))
            return Entitlement.Unknown;

        var key = $"{tenantId}\u001f{userId}\u001f{permission}";
        var now = _time.GetUtcNow().UtcDateTime;
        if (_cache.TryGetValue(key, out var cached) && now - cached.At < FreshFor)
            return cached.Granted ? Entitlement.Granted : Entitlement.Denied;

        try
        {
            var client = _httpClientFactory.CreateClient(HttpClientName);
            var path = $"api/v1/tenants/{Uri.EscapeDataString(tenantId)}/users/{Uri.EscapeDataString(userId)}" +
                       $"/permissions/{Uri.EscapeDataString(permission)}";
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            // Names the tenant for ChoOutboundTokenHandler, so the lookup
            // carries this service's own token when there is no inbound one.
            request.Headers.Add("X-Tenant-ID", tenantId);
            using var response = await client.SendAsync(request, ct);

            // 404 is an answer, not a failure: tenant-service knows this
            // tenant and has no such user in it, so they certainly cannot
            // work the item. Caching it keeps a mistyped id cheap.
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return Remember(key, granted: false, now);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "tenant-service returned {Status} checking {Permission} for user {UserId} in tenant {TenantId}",
                    (int)response.StatusCode, permission, LogSanitizer.Sanitize(userId), LogSanitizer.Sanitize(tenantId));
                return Stale(key, now, permission, userId);
            }

            var granted = ParseGranted(await response.Content.ReadAsStringAsync(ct));
            if (granted is null)
            {
                _logger.LogWarning(
                    "tenant-service answered without a 'granted' flag checking {Permission} for user {UserId} in tenant {TenantId}",
                    permission, LogSanitizer.Sanitize(userId), LogSanitizer.Sanitize(tenantId));
                return Stale(key, now, permission, userId);
            }

            return Remember(key, granted.Value, now);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex,
                "Could not reach tenant-service to check {Permission} for user {UserId} in tenant {TenantId}",
                permission, LogSanitizer.Sanitize(userId), LogSanitizer.Sanitize(tenantId));
            return Stale(key, now, permission, userId);
        }
    }

    private Entitlement Remember(string key, bool granted, DateTime now)
    {
        _cache[key] = (granted, now);
        return granted ? Entitlement.Granted : Entitlement.Denied;
    }

    /// <summary>Last-known-good within the grace window, otherwise no answer.</summary>
    private Entitlement Stale(string key, DateTime now, string permission, string userId)
    {
        if (!_cache.TryGetValue(key, out var cached) || now - cached.At >= StaleGraceFor)
            return Entitlement.Unknown;

        _logger.LogWarning(
            "Using a {Age:0} minute old cached answer for {Permission} / user {UserId}: tenant-service did not answer",
            (now - cached.At).TotalMinutes, permission, LogSanitizer.Sanitize(userId));
        return cached.Granted ? Entitlement.Granted : Entitlement.Denied;
    }

    /// <summary>The <c>granted</c> flag of the reply, or null when it has none.</summary>
    public static bool? ParseGranted(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("granted", out var granted)
                && granted.ValueKind is JsonValueKind.True or JsonValueKind.False)
                return granted.GetBoolean();
        }
        catch (JsonException)
        {
            // Unreadable: the caller treats it as no answer.
        }
        return null;
    }

    /// <summary>Test seam: drops every cached answer.</summary>
    public void Clear() => _cache.Clear();
}
