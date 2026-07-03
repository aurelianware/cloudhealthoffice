using System.Net.Http.Json;
using System.Text.Json;
using BenefitPlanService.Models;
using CloudHealthOffice.Infrastructure.Observability;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace BenefitPlanService.Services;

/// <summary>
/// Adjudication-path provider-integrity gate (capability 5.10 — verification
/// integrity score surface).
///
/// <para>
/// <b>Cached-or-live read pattern.</b> The gate reads the canonical
/// projection on <c>Provider.IntegrityScore</c> from <c>provider-service</c>
/// (<c>GET /api/v1/providers/npi/{npi}</c>) by default. It falls back to
/// the live <c>provider-verification-service</c> path
/// (<c>GET /api/v1/providers/{npi}/integrity-score</c>) when:
/// </para>
/// <list type="bullet">
///   <item>The cached score is null (Provider never refreshed).</item>
///   <item>The cached score is older than
///     <see cref="ProviderIntegrityGateOptions.StalenessFallbackThreshold"/>.</item>
///   <item>The caller explicitly opts in via
///     <c>forceRefresh: true</c>.</item>
/// </list>
///
/// <para>
/// The 1-hour <see cref="IMemoryCache"/> stays as a per-pod
/// request-deduplication layer wrapping the outer
/// <see cref="ProviderIntegrityResult"/>. Cache key is namespaced by the
/// resolution path (<c>cached-or-live</c> vs <c>force-refresh</c>) so a
/// force-refresh call doesn't poison the default-path cache entry. Note:
/// this is a check-then-act pattern, not a true single-flight coalesce —
/// concurrent first-time misses for the same NPI can each fan out to
/// upstream once. A future tightening (e.g. <c>GetOrCreateAsync</c> or a
/// <c>Lazy&lt;Task&lt;...&gt;&gt;</c> per key) would close that window;
/// out of scope for 5.10 because the upstream cost is bounded by the
/// staleness threshold and the typical hot-NPI shape doesn't produce
/// concurrent first-time misses in practice.
/// </para>
///
/// <para>
/// Pass-through results (both upstream services unavailable) are
/// <em>not</em> cached — operators get a recovered exclusion signal on
/// the next adjudication call rather than waiting up to an hour for the
/// cached pass-through to expire.
/// </para>
///
/// <para>
/// Telemetry is emitted per call as
/// <c>cho.provider.integrity_gate.decisions.total</c> with the
/// <c>cho.path</c> dimension set to <c>cached_hit</c>, <c>stale_fallback</c>,
/// <c>null_fallback</c>, or <c>live_only</c>. See
/// <c>docs/architecture/integrity-score-consumption.md</c>.
/// </para>
///
/// <para>
/// On any service failure the gate returns the existing pass-through
/// result so adjudication is never blocked by infrastructure flakes; the
/// scheduled re-verification path in <c>provider-verification-service</c>
/// remains responsible for catching exclusion drift.
/// </para>
/// </summary>
public class HttpProviderIntegrityGate : IProviderIntegrityGate
{
    public const string ProviderServiceClientName = "ProviderService";
    public const string VerificationServiceClientName = "ProviderVerificationService";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMemoryCache _cache;
    private readonly IOptionsMonitor<ProviderIntegrityGateOptions> _options;
    private readonly ILogger<HttpProviderIntegrityGate> _logger;

    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(1);

    public HttpProviderIntegrityGate(
        IHttpClientFactory httpClientFactory,
        IMemoryCache cache,
        IOptionsMonitor<ProviderIntegrityGateOptions> options,
        ILogger<HttpProviderIntegrityGate> logger)
    {
        _httpClientFactory = httpClientFactory;
        _cache = cache;
        _options = options;
        _logger = logger;
    }

    public async Task<ProviderIntegrityResult> CheckAsync(
        string npi,
        bool forceRefresh = false,
        CancellationToken ct = default)
    {
        // Cache key separates default vs force-refresh so a force-refresh
        // call's live-only result doesn't pollute the cached-or-live entry,
        // and vice-versa.
        var cacheKey = forceRefresh
            ? $"provider-integrity:force:{npi}"
            : $"provider-integrity:cached-or-live:{npi}";

        if (_cache.TryGetValue<ProviderIntegrityResult>(cacheKey, out var cached) && cached is not null)
        {
            RecordDecision(IntegrityGatePath.CachedHit, cached.Rating);
            return cached;
        }

        ProviderIntegrityResult result;
        // Track whether the result came from a real data source. Pass-through
        // results (both upstream services down) are NOT cached for the full
        // 1-hour TTL so a recovered exclusion signal is picked up on the next
        // adjudication call rather than waiting an hour. The cache is still
        // useful for real responses (request coalescing) and for genuine
        // pass-throughs we accept a brief retry storm during outages — that's
        // the lesser evil compared to an hour of pretending Excluded providers
        // pass.
        var isPassthrough = false;

        if (forceRefresh)
        {
            var live = await CallVerificationServiceAsync(npi, ct);
            isPassthrough = live is null;
            result = live ?? Passthrough();
            RecordDecision(IntegrityGatePath.LiveOnly, result.Rating);
        }
        else
        {
            // Default path: try the cached projection first.
            var projection = await TryReadProjectionAsync(npi, ct);

            if (projection is null)
            {
                // Provider not found in provider-service or transport
                // failure — fall back to live verification rather than
                // failing closed.
                var live = await CallVerificationServiceAsync(npi, ct);
                isPassthrough = live is null;
                result = live ?? Passthrough();
                RecordDecision(IntegrityGatePath.NullFallback, result.Rating);
            }
            else if (projection.Score is null || projection.LastVerifiedAt is null)
            {
                // Projection row exists but never refreshed — fall back to
                // live and let the local cache absorb subsequent calls.
                result = await CallVerificationServiceAsync(npi, ct)
                    ?? BuildResultFromProjection(projection);
                RecordDecision(IntegrityGatePath.NullFallback, result.Rating);
            }
            else if (IsStale(projection.LastVerifiedAt.Value))
            {
                result = await CallVerificationServiceAsync(npi, ct)
                    ?? BuildResultFromProjection(projection);
                RecordDecision(IntegrityGatePath.StaleFallback, result.Rating);
            }
            else
            {
                result = BuildResultFromProjection(projection);
                RecordDecision(IntegrityGatePath.CachedHit, result.Rating);
            }
        }

        if (!isPassthrough) _cache.Set(cacheKey, result, CacheTtl);
        return result;
    }

    private bool IsStale(DateTimeOffset lastVerifiedAt)
    {
        var threshold = _options.CurrentValue.StalenessFallbackThreshold;
        if (threshold <= TimeSpan.Zero) return false;
        return DateTimeOffset.UtcNow - lastVerifiedAt > threshold;
    }

    private async Task<ProviderProjection?> TryReadProjectionAsync(string npi, CancellationToken ct)
    {
        try
        {
            var client = _httpClientFactory.CreateClient(ProviderServiceClientName);
            var encodedNpi = Uri.EscapeDataString(npi);
            var response = await client.GetAsync(
                $"api/v1/providers/npi/{encodedNpi}", ct);

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return null;

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Provider service returned {StatusCode} for NPI {Npi}; falling back to live verification",
                    response.StatusCode, SanitizeForLog(npi));
                return null;
            }

            return await response.Content.ReadFromJsonAsync<ProviderProjection>(ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex,
                "Provider service unreachable for NPI {Npi}; falling back to live verification",
                SanitizeForLog(npi));
            return null;
        }
    }

    private async Task<ProviderIntegrityResult?> CallVerificationServiceAsync(
        string npi,
        CancellationToken ct)
    {
        try
        {
            var client = _httpClientFactory.CreateClient(VerificationServiceClientName);
            var encodedNpi = Uri.EscapeDataString(npi);
            var response = await client.GetAsync(
                $"api/v1/providers/{encodedNpi}/integrity-score", ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Provider verification service returned {StatusCode} for NPI {Npi}; passing through",
                    response.StatusCode, SanitizeForLog(npi));
                return null;
            }

            var record = await response.Content.ReadFromJsonAsync<IntegrityScoreResponse>(ct);
            if (record is null) return null;

            var rating = NormalizeRating(record.Rating) ?? "Unknown";
            var status = NormalizeStatus(record.Status);
            var isExcluded = status is "Excluded";
            return new ProviderIntegrityResult
            {
                Passed = status is not ("Excluded" or "Failed"),
                IntegrityScore = record.CompositeScore,
                Rating = rating,
                IsExcluded = isExcluded,
                DenialCode = isExcluded ? "B7" : null,
                DenialReason = isExcluded
                    ? "Provider is excluded from federal healthcare programs"
                    : null
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex,
                "Provider verification service unreachable for NPI {Npi}; passing through",
                SanitizeForLog(npi));
            return null;
        }
    }

    private static ProviderIntegrityResult BuildResultFromProjection(ProviderProjection projection)
    {
        // The cached projection captures Score + Rating but not the
        // ExclusionStatus that the live endpoint returns directly. Rating
        // == "Blocked" indicates active exclusion in the verification
        // engine's rubric — translate that into the gate's IsExcluded /
        // DenialCode contract so adjudication denies on cached-only reads.
        var isExcluded = string.Equals(projection.IntegrityRating, "Blocked", StringComparison.OrdinalIgnoreCase);
        return new ProviderIntegrityResult
        {
            Passed = !isExcluded,
            IntegrityScore = projection.IntegrityScore,
            Rating = projection.IntegrityRating ?? "Unknown",
            IsExcluded = isExcluded,
            DenialCode = isExcluded ? "B7" : null,
            DenialReason = isExcluded
                ? "Provider is excluded from federal healthcare programs"
                : null
        };
    }

    private static void RecordDecision(IntegrityGatePath path, string? rating)
    {
        ChoMetrics.ProviderIntegrityGateDecisions.Add(
            1,
            new KeyValuePair<string, object?>("cho.path", PathTag(path)),
            new KeyValuePair<string, object?>("cho.rating", rating ?? "unknown"));
    }

    private static string PathTag(IntegrityGatePath path) => path switch
    {
        IntegrityGatePath.CachedHit      => "cached_hit",
        IntegrityGatePath.StaleFallback  => "stale_fallback",
        IntegrityGatePath.NullFallback   => "null_fallback",
        IntegrityGatePath.LiveOnly       => "live_only",
        _                                => "unknown",
    };

    private static ProviderIntegrityResult Passthrough() => new()
    {
        Passed = true,
        Rating = "Unknown"
    };

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
    }

    private static string? NormalizeRating(JsonElement value)
        => NormalizeEnumValue(value, ["Unknown", "Clear", "Advisory", "Caution", "Alert", "Blocked"]);

    private static string? NormalizeStatus(JsonElement value)
        => NormalizeEnumValue(value, ["Pending", "Verified", "VerifiedWithWarnings", "Failed", "Excluded", "Expired", "ManualReviewRequired"]);

    private static string? NormalizeEnumValue(JsonElement value, IReadOnlyList<string> names)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            var s = value.GetString();
            if (s is not null && names.Contains(s)) return s;
            if (s is not null && int.TryParse(s, out var idx) && idx >= 0 && idx < names.Count) return names[idx];
            return null;
        }
        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var index) && index >= 0 && index < names.Count => names[index],
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => null
        };
    }

    /// <summary>
    /// Subset of the <c>Provider</c> entity returned by
    /// <c>GET /api/v1/providers/npi/{npi}</c>. Only the integrity-projection
    /// fields are bound; everything else on the wire is ignored.
    /// </summary>
    internal sealed record ProviderProjection
    {
        public int? IntegrityScore { get; init; }
        public string? IntegrityRating { get; init; }
        public DateTimeOffset? LastVerifiedAt { get; init; }
        public DateTimeOffset? NextVerificationDue { get; init; }

        // Convenience accessor used by the gate's null-detection branch:
        // the projection row exists but never carried a score.
        internal int? Score => IntegrityScore;
    }

    /// <summary>
    /// Matches the anonymous object shape returned by
    /// GET /api/v1/providers/{npi}/integrity-score on provider-verification-service.
    /// </summary>
    private sealed record IntegrityScoreResponse
    {
        public int CompositeScore { get; init; }
        public JsonElement Rating { get; init; }
        public JsonElement Status { get; init; }
        public DateTimeOffset? VerifiedAt { get; init; }
    }

    private enum IntegrityGatePath
    {
        CachedHit,
        StaleFallback,
        NullFallback,
        LiveOnly,
    }
}
