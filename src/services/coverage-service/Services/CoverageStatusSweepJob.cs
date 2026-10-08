using CoverageService.Models;
using CoverageService.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace CoverageService.Services;

/// <summary>
/// Tunables for <see cref="CoverageStatusSweepJob"/>, bound from
/// <c>CoverageStatusSweep:</c> in configuration.
/// </summary>
public sealed class CoverageStatusSweepOptions
{
    /// <summary>Set false to turn the sweep off.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How often the sweep runs. Default once a day.</summary>
    public int IntervalMinutes { get; set; } = 24 * 60;

    /// <summary>Delay before the first sweep after start-up.</summary>
    public int StartupDelaySeconds { get; set; } = 60;

    /// <summary>Coverages read per query; the sweep repeats until none are left.</summary>
    public int BatchSize { get; set; } = 500;
}

/// <summary>
/// Daily sweep that moves a coverage's current status along its date span
/// (see <see cref="Coverage.DueStatusTransition"/>): a coverage terminated with
/// a future date (834 DTP*349, or the terminate endpoints) stays in force until
/// that date, and this flips it to <see cref="CoverageStatus.Terminated"/> once
/// the date is today or past; a Pending coverage (added ahead of its effective
/// date) is promoted to Active once that date arrives.
///
/// Status hygiene only: date-of-service eligibility is span-based (Pending
/// included from its effective date) and the "currently active" listings
/// derive the same status from the dates themselves, so neither waits on this
/// job. Safe on several replicas: each
/// change is a conditional status update (expected status and dates → new
/// status), so a second replica's write is a no-op, and a coverage reinstated
/// or re-dated in the meantime is left alone. Suspended (and unknown) statuses
/// are never auto-terminated.
/// </summary>
public sealed class CoverageStatusSweepJob : BackgroundService
{
    public const string Actor = "system:coverage-status-sweep";

    private readonly IServiceProvider _services;
    private readonly CoverageStatusSweepOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<CoverageStatusSweepJob> _logger;

    public CoverageStatusSweepJob(
        IServiceProvider services,
        IConfiguration configuration,
        ILogger<CoverageStatusSweepJob> logger)
        : this(services, BindOptions(configuration), TimeProvider.System, logger) { }

    public CoverageStatusSweepJob(
        IServiceProvider services,
        CoverageStatusSweepOptions options,
        TimeProvider clock,
        ILogger<CoverageStatusSweepJob> logger)
    {
        _services = services;
        _options = options;
        _clock = clock;
        _logger = logger;
    }

    private static CoverageStatusSweepOptions BindOptions(IConfiguration configuration)
    {
        var opts = new CoverageStatusSweepOptions();
        configuration.GetSection("CoverageStatusSweep").Bind(opts);
        return opts;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Coverage status sweep is disabled");
            return;
        }

        if (_options.StartupDelaySeconds > 0)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(_options.StartupDelaySeconds), stoppingToken); }
            catch (OperationCanceledException) { return; }
        }

        var interval = TimeSpan.FromMinutes(Math.Max(1, _options.IntervalMinutes));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // One failed sweep must not take the host down; the next one
                // picks up whatever this one missed.
                _logger.LogError(ex, "Coverage status sweep failed; retrying after {Interval}", interval);
            }

            try { await Task.Delay(interval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>One pass over every tenant. Returns how many coverages changed status.</summary>
    public async Task<int> SweepOnceAsync(CancellationToken ct = default)
    {
        using var scope = _services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<ICoverageRepository>();
        var today = _clock.GetUtcNow().UtcDateTime.Date;
        var batchSize = Math.Max(1, _options.BatchSize);

        var changed = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var due = await repository.GetStatusTransitionsDueAsync(today, batchSize) ?? new();

            var changedThisBatch = 0;
            foreach (var coverage in due)
            {
                ct.ThrowIfCancellationRequested();
                var next = coverage.DueStatusTransition(today);
                if (next is null) continue;

                if (await repository.SetStatusAsync(coverage, next.Value, Actor))
                {
                    changedThisBatch++;
                    _logger.LogInformation(
                        "Coverage {CoverageId} (tenant {TenantId}) {From} -> {To} (effective {EffectiveDate:yyyy-MM-dd}, terminates {TerminationDate:yyyy-MM-dd})",
                        coverage.Id, coverage.TenantId, coverage.Status, next.Value, coverage.EffectiveDate, coverage.TerminationDate);
                }
            }
            changed += changedThisBatch;

            // A full batch with no progress would return the same rows again.
            if (due.Count < batchSize || changedThisBatch == 0) break;
        }

        _logger.LogInformation("Coverage status sweep complete for {Today:yyyy-MM-dd}: {Changed} coverage(s) changed", today, changed);
        return changed;
    }
}
