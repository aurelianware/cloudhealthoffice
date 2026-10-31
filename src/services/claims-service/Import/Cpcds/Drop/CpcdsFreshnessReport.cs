using Microsoft.Extensions.Options;

namespace ClaimsService.Import.Cpcds.Drop;

[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
public enum CpcdsFreshness
{
    /// <summary>No drop is configured for the tenant.</summary>
    NotConfigured,

    /// <summary>Configured, but no claim file has loaded yet.</summary>
    NeverLoaded,

    /// <summary>The newest claim extract loaded is within the expected interval.</summary>
    Fresh,

    /// <summary>The newest claim extract loaded is older than expected: the core has stopped sending, or loads are failing.</summary>
    Stale,

    /// <summary>The most recent runs all failed.</summary>
    Failing,
}

/// <summary>
/// Is the tenant's CMS-0057-F data current, and does what loaded add up?
/// Built from the drop runs: when the newest claim extract was written and
/// loaded, how the recent loads went, and money in the files against money
/// loaded. The answer a plan (and its auditor) asks of a core-fed API.
/// </summary>
public sealed class CpcdsFreshnessReport
{
    public string TenantId { get; init; } = string.Empty;
    public DateTime AsOf { get; init; }
    public CpcdsFreshness Status { get; set; }
    public string? SourceSystem { get; init; }
    public TimeSpan? ExpectedEvery { get; init; }

    public string? LastRunId { get; set; }
    public DateTime? LastRunAt { get; set; }
    public CpcdsDropRunStatus? LastRunStatus { get; set; }
    public int ConsecutiveFailedRuns { get; set; }

    /// <summary>When the newest claim file that loaded was written by the core.</summary>
    public DateTime? NewestClaimExtractAt { get; set; }

    /// <summary>How old that extract is now, in hours.</summary>
    public double? ClaimExtractAgeHours { get; set; }
    public DateTime? LastClaimsLoadedAt { get; set; }
    public DateTime? LastMembersLoadedAt { get; set; }

    public int WindowDays { get; init; }
    public CpcdsFreshnessWindow Window { get; } = new();

    /// <summary>What needs someone's attention, in plain words. Empty when nothing does.</summary>
    public List<string> Issues { get; } = [];
}

/// <summary>Totals over the runs in the report's window.</summary>
public sealed class CpcdsFreshnessWindow
{
    public int Runs { get; set; }
    public int FailedRuns { get; set; }
    public int ClaimFilesLoaded { get; set; }
    public decimal ClaimsCreated { get; set; }
    public decimal ClaimsUpdated { get; set; }
    public decimal ClaimsUnchanged { get; set; }
    public decimal ClaimsRejected { get; set; }
    public decimal PaidAmountInFiles { get; set; }
    public decimal PaidAmountLoaded { get; set; }

    /// <summary>Paid in the files but not loaded: the rejected claims' money.</summary>
    public decimal PaidAmountNotLoaded => PaidAmountInFiles - PaidAmountLoaded;
    public decimal MembersCreated { get; set; }
    public decimal MembersUpdated { get; set; }
    public decimal ProvidersCreated { get; set; }
    public decimal ProvidersNotInNppes { get; set; }

    /// <summary>Plan codes unmapped in the most recent member load: coverage for them did not load.</summary>
    public decimal? UnmappedPlanCodesInLastMemberLoad { get; set; }
}

public sealed class CpcdsFreshnessReporter
{
    /// <summary>This many failed runs in a row make the drop Failing.</summary>
    public const int FailingAfter = 3;

    private readonly ICpcdsDropRunRepository _runs;
    private readonly CpcdsDropTenants _tenants;
    private readonly TimeProvider _clock;

    public CpcdsFreshnessReporter(ICpcdsDropRunRepository runs, CpcdsDropTenants tenants, TimeProvider? clock = null)
    {
        _runs = runs;
        _tenants = tenants;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<CpcdsFreshnessReport> BuildAsync(string tenantId, int windowDays, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        windowDays = Math.Clamp(windowDays, 1, 366);
        var tenant = await _tenants.ResolveAsync(tenantId, ct);
        var configured = tenant is not null;

        var report = new CpcdsFreshnessReport
        {
            TenantId = tenantId,
            AsOf = now,
            SourceSystem = tenant?.SourceSystem,
            ExpectedEvery = tenant?.ExpectedEvery,
            WindowDays = windowDays,
        };

        var runs = (await _runs.ListAsync(tenantId, 1000, ct))
            .Where(r => r.Status != CpcdsDropRunStatus.Running)
            .OrderByDescending(r => r.StartedAt)
            .ToList();

        if (runs.FirstOrDefault() is { } last)
        {
            report.LastRunId = last.Id;
            report.LastRunAt = last.StartedAt;
            report.LastRunStatus = last.Status;
        }
        report.ConsecutiveFailedRuns = runs.TakeWhile(r => r.Status == CpcdsDropRunStatus.Failed).Count();

        // Over all recorded runs, not just the window: the newest load may be older than the window.
        var claimLoads = runs
            .SelectMany(r => r.Steps.Where(s => s.Name == "claims" && s.Status != CpcdsDropStepStatus.Failed)
                .Select(s => (Run: r, Step: s)))
            .ToList();
        if (claimLoads.Count > 0)
        {
            var newest = claimLoads
                .Select(l => (l.Run, File: l.Run.Files.FirstOrDefault(f => l.Step.Files.Contains(f.Name))))
                .Where(x => x.File is not null)
                .OrderByDescending(x => x.File!.LastWriteUtc)
                .FirstOrDefault();
            report.NewestClaimExtractAt = newest.File?.LastWriteUtc;
            report.LastClaimsLoadedAt = claimLoads.Max(l => l.Run.CompletedAt ?? l.Run.StartedAt);
            if (report.NewestClaimExtractAt is { } at)
                report.ClaimExtractAgeHours = Math.Round((now - at).TotalHours, 1);
        }
        report.LastMembersLoadedAt = runs
            .Where(r => r.Steps.Any(s => s.Name == "members-and-coverage" && s.Status != CpcdsDropStepStatus.Failed))
            .Select(r => (DateTime?)(r.CompletedAt ?? r.StartedAt))
            .FirstOrDefault();

        var since = now.AddDays(-windowDays);
        var window = report.Window;
        foreach (var run in runs.Where(r => r.StartedAt >= since))
        {
            window.Runs++;
            if (run.Status == CpcdsDropRunStatus.Failed) window.FailedRuns++;
            foreach (var step in run.Steps.Where(s => s.Status != CpcdsDropStepStatus.Failed))
            {
                switch (step.Name)
                {
                    case "claims":
                        window.ClaimFilesLoaded++;
                        window.ClaimsCreated += Get(step, "created");
                        window.ClaimsUpdated += Get(step, "updated");
                        window.ClaimsUnchanged += Get(step, "unchanged");
                        window.ClaimsRejected += Get(step, "rejected");
                        window.PaidAmountInFiles += Get(step, "paidAmountInFile");
                        window.PaidAmountLoaded += Get(step, "paidAmountLoaded");
                        break;
                    case "members-and-coverage":
                        window.MembersCreated += Get(step, "membersCreated") + Get(step, "dependentsCreated");
                        window.MembersUpdated += Get(step, "membersUpdated") + Get(step, "dependentsUpdated");
                        break;
                    case "claim-providers":
                        window.ProvidersCreated += Get(step, "created");
                        window.ProvidersNotInNppes += Get(step, "notInNppes");
                        break;
                }
            }
        }
        var lastMembers = runs.SelectMany(r => r.Steps)
            .FirstOrDefault(s => s.Name == "members-and-coverage" && s.Status != CpcdsDropStepStatus.Failed);
        if (lastMembers is not null)
            window.UnmappedPlanCodesInLastMemberLoad = Get(lastMembers, "unmappedPlanCodes");

        report.Status = !configured ? CpcdsFreshness.NotConfigured
            : report.ConsecutiveFailedRuns >= FailingAfter ? CpcdsFreshness.Failing
            : report.NewestClaimExtractAt is null ? CpcdsFreshness.NeverLoaded
            : now - report.NewestClaimExtractAt > tenant!.ExpectedEvery + tenant.StaleGrace ? CpcdsFreshness.Stale
            : CpcdsFreshness.Fresh;

        AddIssues(report);
        return report;
    }

    private static decimal Get(CpcdsDropStep step, string name) => step.Counts.GetValueOrDefault(name);

    private static void AddIssues(CpcdsFreshnessReport report)
    {
        var w = report.Window;
        switch (report.Status)
        {
            case CpcdsFreshness.NotConfigured:
                report.Issues.Add("No CPCDS drop is configured for this tenant.");
                break;
            case CpcdsFreshness.NeverLoaded:
                report.Issues.Add("No claim file has loaded yet; members' apps show no core claims.");
                break;
            case CpcdsFreshness.Failing:
                report.Issues.Add($"The last {report.ConsecutiveFailedRuns} drop runs failed; see the runs' step errors.");
                break;
            case CpcdsFreshness.Stale:
                report.Issues.Add(
                    $"The newest claim extract loaded is {report.ClaimExtractAgeHours:0.#} hours old, longer than the core's expected interval; " +
                    "check that the core is still sending and that runs are succeeding.");
                break;
        }
        if (w.ClaimsRejected > 0)
            report.Issues.Add($"{w.ClaimsRejected:0} claims were refused in the last {report.WindowDays} days " +
                              $"({w.PaidAmountNotLoaded:0.00} paid not loaded); members do not see them.");
        if (w.UnmappedPlanCodesInLastMemberLoad > 0)
            report.Issues.Add($"{w.UnmappedPlanCodesInLastMemberLoad:0} plan codes in the last member load have no CHO benefit plan; " +
                              "coverage for them did not load.");
        if (w.ProvidersNotInNppes > 0)
            report.Issues.Add($"{w.ProvidersNotInNppes:0} provider NPIs on claims are not in NPPES; their EOBs carry the NPI only.");
    }
}
