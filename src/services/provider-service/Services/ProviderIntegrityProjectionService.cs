using Microsoft.Extensions.Options;
using ProviderService.Models;
using ProviderService.Repositories;

namespace ProviderService.Services;

/// <summary>
/// Drives the verification → write-back projection pipeline (capability 5.4.5).
///
/// <list type="number">
///   <item>Resolve due providers in a tenant via
///     <see cref="IProviderRepository.ListProvidersForIntegrityRefreshAsync"/>.</item>
///   <item>Batch their NPIs into <c>POST /verify/batch</c> calls (100/batch).</item>
///   <item>Patch <c>IntegrityScore</c>/<c>IntegrityRating</c>/
///     <c>LastVerifiedAt</c>/<c>NextVerificationDue</c> onto each head Active
///     row via
///     <see cref="IProviderRepository.UpdateIntegrityProjectionAsync"/>.</item>
///   <item>Emit a deterministic <c>ProviderVerificationRefreshed</c> event per
///     row via <see cref="IProviderVerificationEventPublisher"/>.</item>
/// </list>
///
/// <para>
/// Failure isolation: a verification-source outage returns an empty result
/// for the batch — already-cached scores stay put, the row's
/// <c>NextVerificationDue</c> is left alone so the next sweep retries.
/// A patch failure on one row does not abort the batch.
/// </para>
/// </summary>
public interface IProviderIntegrityProjectionService
{
    /// <summary>
    /// Refresh a single provider on demand. Returns the patched score
    /// and metadata, or null when the provider has no Active head.
    /// Used by <c>POST /api/v1/providers/{id}/verification/refresh</c>.
    /// </summary>
    Task<IntegrityProjectionRefreshResult?> RefreshProviderAsync(
        string tenantId,
        string providerId,
        bool forceRefresh,
        string? actorId,
        string? correlationId,
        CancellationToken ct = default);

    /// <summary>
    /// Refresh all due providers in a tenant. Returns the per-tenant
    /// telemetry: providers inspected vs. patched vs. skipped vs. failed.
    /// Used by both the hosted worker and the admin backfill endpoint.
    /// </summary>
    Task<IntegrityProjectionTenantSweepResult> RefreshTenantAsync(
        string tenantId,
        IntegrityProjectionTenantSweepRequest request,
        CancellationToken ct = default);
}

public sealed class ProviderIntegrityProjectionService : IProviderIntegrityProjectionService
{
    private readonly IProviderRepository _providers;
    private readonly IProviderVerificationClient _verification;
    private readonly IProviderVerificationEventPublisher _events;
    private readonly IOptions<IntegrityProjectionOptions> _options;
    private readonly ILogger<ProviderIntegrityProjectionService> _logger;

    public ProviderIntegrityProjectionService(
        IProviderRepository providers,
        IProviderVerificationClient verification,
        IProviderVerificationEventPublisher events,
        IOptions<IntegrityProjectionOptions> options,
        ILogger<ProviderIntegrityProjectionService> logger)
    {
        _providers = providers;
        _verification = verification;
        _events = events;
        _options = options;
        _logger = logger;
    }

    public async Task<IntegrityProjectionRefreshResult?> RefreshProviderAsync(
        string tenantId,
        string providerId,
        bool forceRefresh,
        string? actorId,
        string? correlationId,
        CancellationToken ct = default)
    {
        // The on-demand path bypasses the due-date filter when
        // forceRefresh is true; otherwise it still consults
        // NextVerificationDue so callers can re-trigger without paying
        // for an unnecessary live verification.
        var head = await _providers.GetLatestActiveAsync(providerId, DateTime.UtcNow);
        if (head == null) return null;
        if (head.TenantId != tenantId) return null;

        if (!forceRefresh
            && head.NextVerificationDue.HasValue
            && head.NextVerificationDue.Value > DateTimeOffset.UtcNow)
        {
            return new IntegrityProjectionRefreshResult
            {
                ProviderId = head.ProviderId,
                Skipped = true,
                IntegrityScore = head.IntegrityScore,
                IntegrityRating = head.IntegrityRating,
                LastVerifiedAt = head.LastVerifiedAt,
                NextVerificationDue = head.NextVerificationDue,
            };
        }

        var results = await _verification.VerifyBatchAsync(new[] { head.NPI }, ct);
        var match = results.FirstOrDefault(r => r.Npi == head.NPI);
        if (match == null)
        {
            _logger.LogInformation(
                "verification refresh produced no result for {ProviderId}; preserving cached projection",
                Sanitize(providerId));
            return new IntegrityProjectionRefreshResult
            {
                ProviderId = head.ProviderId,
                Skipped = false,
                Failed = true,
                IntegrityScore = head.IntegrityScore,
                IntegrityRating = head.IntegrityRating,
                LastVerifiedAt = head.LastVerifiedAt,
                NextVerificationDue = head.NextVerificationDue,
            };
        }

        return await ApplyAsync(tenantId, head.ProviderId, match, actorId, correlationId, ct);
    }

    public async Task<IntegrityProjectionTenantSweepResult> RefreshTenantAsync(
        string tenantId,
        IntegrityProjectionTenantSweepRequest request,
        CancellationToken ct = default)
    {
        var opts = _options.Value;
        var result = new IntegrityProjectionTenantSweepResult { TenantId = tenantId };

        // Per-sweep cap protects round-robin fairness across tenants when
        // the worker is the caller; the admin backfill passes its own
        // request.MaxProviders to bypass this for one-shot work.
        var maxProviders = request.MaxProviders ?? opts.MaxProvidersPerTenantPerSweep;
        var pageSize = Math.Clamp(request.PageSize ?? opts.PageSize, 1, 100);
        var includeNeverVerified = request.IncludeNeverVerified;
        var dueBefore = request.DueBefore ?? DateTimeOffset.UtcNow;
        var refreshWindow = opts.ShortestActiveWindow();

        var skip = 0;
        while (result.Patched + result.Failed + result.Skipped < maxProviders)
        {
            ct.ThrowIfCancellationRequested();
            var page = await _providers.ListProvidersForIntegrityRefreshAsync(
                tenantId, dueBefore, includeNeverVerified, skip, pageSize, ct);
            if (page.Count == 0) break;

            // De-dupe NPIs before the batch call: the (TenantId, NPI)
            // index isn't unique, so a tenant can legitimately have
            // multiple Provider rows sharing an NPI (chain history,
            // genuine duplicates, data-quality drift). ToDictionary on
            // a duplicated key would crash the whole sweep.
            var npis = page.Select(p => p.NPI).Distinct().ToList();

            var verificationResults = await _verification.VerifyBatchAsync(npis, ct);
            // GroupBy + First defends against the verification service
            // returning duplicate records per NPI (server contract is
            // one-per-NPI, but we shouldn't crash on contract drift).
            var resultByNpi = verificationResults
                .GroupBy(r => r.Npi)
                .ToDictionary(g => g.Key, g => g.First());

            foreach (var p in page)
            {
                if (result.Patched + result.Failed + result.Skipped >= maxProviders) break;

                if (!resultByNpi.TryGetValue(p.NPI, out var match))
                {
                    // Verification source returned no record for this
                    // NPI — preserve cached projection and let the next
                    // sweep retry.
                    result.Failed++;
                    continue;
                }

                try
                {
                    var apply = await ApplyAsync(
                        tenantId, p.ProviderId, match,
                        actorId: request.ActorId,
                        correlationId: request.CorrelationId,
                        ct);
                    if (apply == null) result.Skipped++;
                    else if (apply.Failed) result.Failed++;
                    else result.Patched++;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "integrity projection write-back failed for {Tenant}/{Provider}",
                        Sanitize(tenantId), Sanitize(p.ProviderId));
                    result.Failed++;
                }
            }

            // Avoid an infinite loop if the dueBefore filter still
            // matches the same page after writes (in practice the patch
            // moves NextVerificationDue past dueBefore, so the filter
            // re-skips them — but we belt-and-suspenders skip forward).
            skip += page.Count;
            if (page.Count < pageSize) break;
        }

        result.RefreshWindow = refreshWindow;
        return result;
    }

    private async Task<IntegrityProjectionRefreshResult> ApplyAsync(
        string tenantId,
        string providerId,
        VerificationResult match,
        string? actorId,
        string? correlationId,
        CancellationToken ct)
    {
        var verifiedAt = match.LastVerifiedAt;
        var nextDue = match.NextScheduledVerification
            ?? verifiedAt + _options.Value.ShortestActiveWindow();
        var score = match.IntegrityScore?.CompositeScore;
        var rating = match.IntegrityScore?.Rating;

        var patched = await _providers.UpdateIntegrityProjectionAsync(
            tenantId, providerId, score, rating, verifiedAt, nextDue, ct);
        if (!patched)
        {
            return new IntegrityProjectionRefreshResult
            {
                ProviderId = providerId,
                Skipped = true,
                IntegrityScore = score,
                IntegrityRating = rating,
                LastVerifiedAt = verifiedAt,
                NextVerificationDue = nextDue,
            };
        }

        try
        {
            await _events.PublishRefreshedAsync(
                tenantId, providerId, score, rating, verifiedAt, nextDue,
                actorId, correlationId, ct);
        }
        catch (Exception ex)
        {
            // Event publication is best-effort; the patch already landed.
            // Re-emitting on the next sweep is idempotent (deterministic
            // EventId on (providerId, verifiedAt)).
            _logger.LogWarning(ex,
                "verification event publication failed for {Tenant}/{Provider}; patch already applied",
                Sanitize(tenantId), Sanitize(providerId));
        }

        return new IntegrityProjectionRefreshResult
        {
            ProviderId = providerId,
            IntegrityScore = score,
            IntegrityRating = rating,
            LastVerifiedAt = verifiedAt,
            NextVerificationDue = nextDue,
        };
    }

    private static string Sanitize(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", string.Empty).Replace("\n", string.Empty);
}

/// <summary>
/// Per-provider refresh outcome surfaced by the on-demand endpoint.
/// </summary>
public sealed class IntegrityProjectionRefreshResult
{
    public string ProviderId { get; set; } = string.Empty;
    public int? IntegrityScore { get; set; }
    public string? IntegrityRating { get; set; }
    public DateTimeOffset? LastVerifiedAt { get; set; }
    public DateTimeOffset? NextVerificationDue { get; set; }
    public bool Skipped { get; set; }
    public bool Failed { get; set; }
}

/// <summary>
/// Per-tenant sweep request used by both the hosted worker and the admin
/// backfill endpoint. The same machinery serves both paths; the request
/// shape distinguishes them via <see cref="IncludeNeverVerified"/> and
/// <see cref="MaxProviders"/>.
/// </summary>
public sealed class IntegrityProjectionTenantSweepRequest
{
    /// <summary>Cutoff for "due now". Defaults to <see cref="DateTimeOffset.UtcNow"/>.</summary>
    public DateTimeOffset? DueBefore { get; set; }

    /// <summary>
    /// True when null <c>NextVerificationDue</c> rows should be swept
    /// (legacy / never-verified). Worker = true; admin backfill = true.
    /// </summary>
    public bool IncludeNeverVerified { get; set; } = true;

    /// <summary>
    /// Optional override for the per-sweep cap. Worker passes null
    /// (uses <see cref="IntegrityProjectionOptions.MaxProvidersPerTenantPerSweep"/>);
    /// admin backfill can pass a higher value.
    /// </summary>
    public int? MaxProviders { get; set; }

    public int? PageSize { get; set; }

    public string? ActorId { get; set; }
    public string? CorrelationId { get; set; }
}

/// <summary>
/// Telemetry surfaced by per-tenant sweeps. Worker logs aggregate; admin
/// backfill returns this in the HTTP response body so operators can
/// confirm the run.
/// </summary>
public sealed class IntegrityProjectionTenantSweepResult
{
    public string TenantId { get; set; } = string.Empty;
    public int Patched { get; set; }
    public int Skipped { get; set; }
    public int Failed { get; set; }
    public TimeSpan RefreshWindow { get; set; }
    public int Inspected => Patched + Skipped + Failed;
}
