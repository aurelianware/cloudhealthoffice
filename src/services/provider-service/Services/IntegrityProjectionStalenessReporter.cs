using CloudHealthOffice.Infrastructure.Observability;
using Microsoft.Extensions.Options;
using ProviderService.Models;
using ProviderService.Repositories;

namespace ProviderService.Services;

/// <summary>
/// Per-tenant staleness telemetry for cached integrity projections
/// (capability 5.10 — verification integrity score surface).
///
/// <para>
/// Piggybacks on <c>IntegrityProjectionWorker</c>'s existing sweep —
/// the worker invokes <see cref="ReportTenantAsync"/> once per tenant
/// per sweep, after the refresh pass. The reporter queries the
/// repository for the count of head-Active providers whose
/// <c>LastVerifiedAt</c> is older than
/// <see cref="IntegrityProjectionOptions.StalenessAlertThreshold"/>
/// and updates the per-tenant snapshot read by the
/// <c>cho.provider.integrity_score.stale_count</c> Prometheus gauge in
/// <see cref="ChoMetrics.ProviderIntegrityScoreStaleCount"/>.
/// </para>
///
/// <para>
/// No new hosted service is introduced — Decision 3 of capability 5.10
/// pins consolidation over infrastructure expansion. The reporter
/// itself is stateless; the per-tenant snapshot lives on
/// <see cref="ChoMetrics"/> so the gauge can read it on each scrape.
/// </para>
/// </summary>
public interface IIntegrityProjectionStalenessReporter
{
    /// <summary>
    /// Compute the per-tenant stale-provider count and update the
    /// <c>cho.provider.integrity_score.stale_count</c> gauge snapshot.
    /// Returns the count for caller-side telemetry / structured logging;
    /// callers may safely ignore the return value.
    ///
    /// <para>
    /// When <see cref="IntegrityProjectionOptions.StalenessAlertThreshold"/>
    /// is non-positive the gauge entry for the tenant is cleared and
    /// the count returns <c>0</c> — operators get an explicit "off"
    /// signal rather than a stale snapshot.
    /// </para>
    /// </summary>
    Task<long> ReportTenantAsync(string tenantId, CancellationToken ct = default);
}

/// <inheritdoc cref="IIntegrityProjectionStalenessReporter" />
public sealed class IntegrityProjectionStalenessReporter : IIntegrityProjectionStalenessReporter
{
    private readonly IProviderRepository _providers;
    private readonly IOptionsMonitor<IntegrityProjectionOptions> _options;
    private readonly ILogger<IntegrityProjectionStalenessReporter> _logger;

    public IntegrityProjectionStalenessReporter(
        IProviderRepository providers,
        IOptionsMonitor<IntegrityProjectionOptions> options,
        ILogger<IntegrityProjectionStalenessReporter> logger)
    {
        _providers = providers;
        _options = options;
        _logger = logger;
    }

    public async Task<long> ReportTenantAsync(string tenantId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(tenantId))
            throw new ArgumentException("tenantId is required.", nameof(tenantId));

        var threshold = _options.CurrentValue.StalenessAlertThreshold;
        if (threshold <= TimeSpan.Zero)
        {
            // Threshold disabled — drop the gauge entry rather than
            // hold a stale snapshot. -1 sentinel triggers TryRemove.
            ChoMetrics.SetIntegrityScoreStaleCount(tenantId, -1);
            return 0;
        }

        var staleBefore = DateTimeOffset.UtcNow - threshold;

        try
        {
            var count = await _providers.CountStaleProvidersAsync(tenantId, staleBefore, ct);
            ChoMetrics.SetIntegrityScoreStaleCount(tenantId, count);
            return count;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Telemetry must never block the sweep. Log and continue —
            // the next sweep refreshes the snapshot.
            _logger.LogWarning(ex,
                "IntegrityProjectionStalenessReporter failed for tenant {Tenant}; gauge snapshot unchanged",
                Sanitize(tenantId));
            return 0;
        }
    }

    private static string Sanitize(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", string.Empty).Replace("\n", string.Empty);
}
