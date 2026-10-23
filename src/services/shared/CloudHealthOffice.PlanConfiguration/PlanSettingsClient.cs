using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using CloudHealthOffice.Infrastructure.Logging;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace CloudHealthOffice.PlanConfiguration;

/// <summary>
/// Reads settings for a service (plan-configuration.md §9). Never throws for
/// an unreachable or misconfigured tenant-service: it answers with the
/// definition's default for the line of business, marked
/// <see cref="ResolvedSetting.IsFallback"/>, and logs a warning. Callers that
/// should behave more cautiously on a fallback check that flag.
/// </summary>
public interface IPlanSettings
{
    /// <summary>Resolves a defined setting. Throws only for a key that is not in <see cref="PlanSettingCatalog"/> (a programming error).</summary>
    Task<ResolvedSetting> ResolveAsync(string tenantId, string key, SettingScope scope, DateTime? asOf = null, CancellationToken ct = default);

    /// <summary>Drops cached answers for a tenant's key (on <c>configuration.changed.v1</c>).</summary>
    void Invalidate(string tenantId, string key);
}

public sealed class PlanSettingsOptions
{
    public const string SectionName = "PlanSettings";

    /// <summary>How long a resolved value is cached when no change event arrives.</summary>
    public int CacheSeconds { get; set; } = 60;
}

/// <summary>
/// Caches resolved values per tenant, key, scope and date; a change to a key
/// evicts every cached answer for it (one change token per tenant and key).
/// </summary>
public abstract class CachingPlanSettings(IMemoryCache cache, PlanSettingsOptions options, TimeProvider clock, ILogger logger) : IPlanSettings
{
    private readonly object _gate = new();
    private readonly Dictionary<string, CancellationTokenSource> _tokens = new(StringComparer.Ordinal);

    public async Task<ResolvedSetting> ResolveAsync(string tenantId, string key, SettingScope scope, DateTime? asOf = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        var definition = PlanSettingCatalog.Require(key);
        var date = (asOf ?? clock.GetUtcNow().UtcDateTime).Date;
        var cacheKey = $"plan-setting|{tenantId}|{key}|{scope.CacheKey}|{date:yyyy-MM-dd}";
        if (cache.TryGetValue(cacheKey, out ResolvedSetting? cached) && cached is not null) return cached;

        ResolvedSetting resolved;
        try
        {
            resolved = await FetchAsync(tenantId, definition, scope, date, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Could not read setting {Key} for tenant {TenantId}; using the default for its line of business",
                LogSanitizer.Sanitize(key), LogSanitizer.Sanitize(tenantId));
            // Not cached: the next call tries again.
            return SettingResolver.Resolve(definition, scope, date, [], isFallback: true);
        }

        var entry = new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(Math.Max(1, options.CacheSeconds)) };
        entry.AddExpirationToken(new CancellationChangeToken(TokenFor(tenantId, key).Token));
        cache.Set(cacheKey, resolved, entry);
        return resolved;
    }

    public void Invalidate(string tenantId, string key)
    {
        CancellationTokenSource? old;
        lock (_gate)
        {
            var id = $"{tenantId}|{key}";
            _tokens.Remove(id, out old);
        }
        old?.Cancel();
        old?.Dispose();
    }

    private CancellationTokenSource TokenFor(string tenantId, string key)
    {
        lock (_gate)
        {
            var id = $"{tenantId}|{key}";
            if (!_tokens.TryGetValue(id, out var cts)) _tokens[id] = cts = new CancellationTokenSource();
            return cts;
        }
    }

    protected abstract Task<ResolvedSetting> FetchAsync(string tenantId, SettingDefinition definition, SettingScope scope, DateTime asOf, CancellationToken ct);
}

/// <summary>Resolves through tenant-service's <c>GET api/v1/configuration/resolve</c>.</summary>
public sealed class HttpPlanSettings(
    IHttpClientFactory httpClientFactory,
    IMemoryCache cache,
    PlanSettingsOptions options,
    TimeProvider clock,
    ILogger<HttpPlanSettings> logger) : CachingPlanSettings(cache, options, clock, logger)
{
    public const string HttpClientName = "plan-settings";

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    protected override async Task<ResolvedSetting> FetchAsync(string tenantId, SettingDefinition definition, SettingScope scope, DateTime asOf, CancellationToken ct)
    {
        var query = $"api/v1/configuration/resolve?key={Uri.EscapeDataString(definition.Key)}&asOf={asOf:yyyy-MM-dd}";
        if (LinesOfBusiness.Normalize(scope.LineOfBusiness) is { } lob) query += $"&lineOfBusiness={Uri.EscapeDataString(lob)}";
        if (!string.IsNullOrWhiteSpace(scope.Sponsor)) query += $"&sponsor={Uri.EscapeDataString(scope.Sponsor.Trim())}";

        var client = httpClientFactory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, query);
        // With no caller, ChoOutboundTokenHandler mints a service token for this tenant.
        request.Headers.Add("X-Tenant-ID", tenantId);
        using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ResolvedSetting>(Json, ct).ConfigureAwait(false)
               ?? throw new InvalidOperationException("tenant-service returned an empty setting.");
    }
}

/// <summary>
/// Settings held in memory and resolved with the same rules as tenant-service:
/// for tests, and for hosts that run without tenant-service.
/// </summary>
public sealed class InMemoryPlanSettings : IPlanSettings
{
    private readonly object _gate = new();
    private readonly List<SettingValue> _values = new();

    /// <summary>Set to make every call fail, as an unreachable tenant-service would (answers fall back).</summary>
    public bool Unavailable { get; set; }

    public int Calls { get; private set; }

    public void Set(string tenantId, string key, SettingLevel level, string scopeId, string value,
        DateTime? effectiveFrom = null, bool locked = false, DateTime? effectiveTo = null)
    {
        lock (_gate)
        {
            var version = _values.Where(v => v.TenantId == tenantId && v.Key == key && v.Level == level && v.ScopeId == scopeId)
                .Select(v => v.Version).DefaultIfEmpty(0).Max() + 1;
            _values.Add(new SettingValue
            {
                Id = SettingValue.IdFor(tenantId, key, level, scopeId, version),
                TenantId = tenantId, Key = key, Level = level, ScopeId = scopeId, Value = value, Locked = locked,
                EffectiveFrom = (effectiveFrom ?? DateTime.MinValue).Date, EffectiveTo = effectiveTo?.Date,
                Version = version, SetBy = "test", SetAt = DateTime.UtcNow,
            });
        }
    }

    public Task<ResolvedSetting> ResolveAsync(string tenantId, string key, SettingScope scope, DateTime? asOf = null, CancellationToken ct = default)
    {
        var definition = PlanSettingCatalog.Require(key);
        var date = (asOf ?? DateTime.UtcNow).Date;
        lock (_gate)
        {
            Calls++;
            if (Unavailable) return Task.FromResult(SettingResolver.Resolve(definition, scope, date, [], isFallback: true));
            return Task.FromResult(SettingResolver.Resolve(definition, scope, date, _values.Where(v => v.TenantId == tenantId).ToList()));
        }
    }

    public void Invalidate(string tenantId, string key) { }
}
