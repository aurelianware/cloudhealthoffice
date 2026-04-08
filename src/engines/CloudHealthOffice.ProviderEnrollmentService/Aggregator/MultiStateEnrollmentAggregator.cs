using CloudHealthOffice.ProviderEnrollmentService.Abstractions;
using CloudHealthOffice.ProviderEnrollmentService.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CloudHealthOffice.ProviderEnrollmentService.Aggregator;

/// <summary>
/// Fans out enrollment lookups to all registered IStateEnrollmentSource implementations
/// and assembles a ProviderEnrollmentSummary with cross-state gap detection.
///
/// Registered as scoped — sources are resolved via DI and filtered by
/// ProviderEnrollmentOptions.EnabledStateCodes at runtime.
/// </summary>
public sealed class MultiStateEnrollmentAggregator
{
    private readonly IReadOnlyList<IStateEnrollmentSource> _sources;
    private readonly ProviderEnrollmentOptions _opts;
    private readonly ILogger<MultiStateEnrollmentAggregator> _logger;

    public MultiStateEnrollmentAggregator(
        IEnumerable<IStateEnrollmentSource> sources,
        IOptions<ProviderEnrollmentOptions> options,
        ILogger<MultiStateEnrollmentAggregator> logger)
    {
        _opts    = options.Value;
        _logger  = logger;

        // Filter to enabled state codes if configured; else use all registered
        _sources = (_opts.EnabledStateCodes.Count > 0
            ? sources.Where(s => _opts.EnabledStateCodes.Contains(s.StateCode, StringComparer.OrdinalIgnoreCase))
            : sources).ToList();

        _logger.LogInformation(
            "MultiStateEnrollmentAggregator initialized with {Count} sources: {States}",
            _sources.Count,
            string.Join(", ", _sources.Select(s => s.StateCode)));
    }

    // ── Cross-state profile ───────────────────────────────────────

    /// <summary>
    /// Query all enabled state sources in parallel and assemble a cross-state summary.
    /// Used for provider onboarding review, panel reconciliation, and the CHO portal.
    /// </summary>
    public async Task<ProviderEnrollmentSummary> GetCrossStateProfileAsync(
        string npi,
        CancellationToken ct = default)
    {
        var asOfDate = DateOnly.FromDateTime(DateTime.UtcNow);

        var tasks = _sources.Select(async source =>
        {
            try
            {
                return await source.GetEnrollmentAsync(npi, asOfDate, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Enrollment source {State}/{System} failed for NPI {Npi}",
                    source.StateCode, source.SourceSystemName, npi);
                return null;
            }
        });

        var results = await Task.WhenAll(tasks);
        var records = results.Where(r => r is not null).Cast<StateEnrollmentRecord>().ToList();

        return BuildSummary(npi, records);
    }

    /// <summary>
    /// Query a single state source by state code.
    /// Useful when the caller already knows the relevant state (e.g., PA gate for TX claim).
    /// </summary>
    public async Task<StateEnrollmentRecord?> GetEnrollmentForStateAsync(
        string npi,
        string stateCode,
        CancellationToken ct = default)
    {
        var source = _sources.FirstOrDefault(s =>
            s.StateCode.Equals(stateCode, StringComparison.OrdinalIgnoreCase));

        if (source is null)
        {
            _logger.LogWarning("No enrollment source registered for state {StateCode}", SanitizeForLog(stateCode));
            return null;
        }

        return await source.GetEnrollmentAsync(npi, DateOnly.FromDateTime(DateTime.UtcNow), ct);
    }

    /// <summary>
    /// Panel-level reconciliation across all enabled states.
    /// Designed for nightly batch execution — returns summaries for every NPI.
    /// </summary>
    public async Task<IReadOnlyList<ProviderEnrollmentSummary>> ReconcilePanelAsync(
        IEnumerable<string> npis,
        CancellationToken ct = default)
    {
        var asOfDate = DateOnly.FromDateTime(DateTime.UtcNow);
        var npiList  = npis.ToList();

        _logger.LogInformation(
            "Panel reconciliation starting: {Count} NPIs across {Sources} sources",
            npiList.Count, _sources.Count);

        // Fan out: for each source, get the whole panel, then group by NPI
        var allRecords = new List<StateEnrollmentRecord>();

        foreach (var source in _sources)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var records = await source.GetPanelAsync(npiList, asOfDate, ct);
                allRecords.AddRange(records);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Panel fetch failed for {State}/{System}",
                    source.StateCode, source.SourceSystemName);
            }
        }

        // Group by NPI and build summaries
        return allRecords
            .GroupBy(r => r.Npi)
            .Select(g => BuildSummary(g.Key, g.ToList()))
            .ToList();
    }

    // ── Gap detection ─────────────────────────────────────────────

    private ProviderEnrollmentSummary BuildSummary(
        string npi, IReadOnlyList<StateEnrollmentRecord> records)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        var active     = records.Where(r => r.Status == EnrollmentStatus.Active).ToList();
        var pending    = records.Where(r => r.Status == EnrollmentStatus.Pending).ToList();
        var terminated = records.Where(r => r.Status == EnrollmentStatus.Terminated).ToList();

        var gaps  = DetectGaps(records);
        var risks = DetectRevalidationRisks(records, today);

        return new ProviderEnrollmentSummary
        {
            Npi              = npi,
            ActiveStates     = active.Select(r => r.StateCode).Distinct().ToList(),
            PendingStates    = pending.Select(r => r.StateCode).Distinct().ToList(),
            TerminatedStates = terminated.Select(r => r.StateCode).Distinct().ToList(),
            AllRecords       = records,
            EnrollmentGaps   = gaps,
            RevalidationRisks = risks
        };
    }

    private static IReadOnlyList<EnrollmentGap> DetectGaps(
        IReadOnlyList<StateEnrollmentRecord> records)
    {
        var gaps = new List<EnrollmentGap>();

        // Revalidation overdue — active status but revalidation date passed
        foreach (var r in records.Where(r => r.Status == EnrollmentStatus.Active))
        {
            if (r.RevalidationDueDate.HasValue &&
                r.RevalidationDueDate.Value < DateOnly.FromDateTime(DateTime.UtcNow))
            {
                gaps.Add(new EnrollmentGap
                {
                    StateCode   = r.StateCode,
                    Type        = EnrollmentGapType.RevalidationOverdue,
                    Description = $"Revalidation was due {r.RevalidationDueDate.Value:d} " +
                                  $"but provider still shows Active in {r.SourceSystem}"
                });
            }
        }

        // Note: ActiveContractNoEnrollment and ActiveEnrollmentNoContract gaps require
        // access to the CHO ProviderContract master record. Those are detected by the
        // McoPanelReconciliationService which joins this service with the contract store.

        return gaps;
    }

    private IReadOnlyList<RevalidationRisk> DetectRevalidationRisks(
        IReadOnlyList<StateEnrollmentRecord> records, DateOnly today)
    {
        return records
            .Where(r => r.RevalidationDueDate.HasValue &&
                        r.RevalidationDueDate.Value > today &&
                        (r.RevalidationDueDate.Value.DayNumber - today.DayNumber) <= _opts.RevalidationWarningDays)
            .Select(r => new RevalidationRisk
            {
                StateCode           = r.StateCode,
                SourceSystem        = r.SourceSystem,
                RevalidationDueDate = r.RevalidationDueDate!.Value,
                DaysRemaining       = r.RevalidationDueDate!.Value.DayNumber - today.DayNumber
            })
            .OrderBy(r => r.DaysRemaining)
            .ToList();
    }

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
    }
}
