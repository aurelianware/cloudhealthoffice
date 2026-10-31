using ClaimsService.Import.Cpcds.Drop;
using FluentAssertions;
using Microsoft.Extensions.Options;

namespace CloudHealthOffice.ClaimsService.Tests.Import.Cpcds.Drop;

public class CpcdsFreshnessReporterTests
{
    private const string Tenant = "tenant-a";
    private static readonly DateTime Now = new(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);

    private readonly InMemoryCpcdsDropRunRepository _runs = new();
    private readonly CpcdsDropOptions _options = new()
    {
        Enabled = true,
        Tenants = { [Tenant] = new CpcdsDropTenantOptions { SourceSystem = "qnxt" } },
    };

    private sealed class Clock(DateTime now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(now);
    }

    private Task<CpcdsFreshnessReport> Report(int days = 30) =>
        new CpcdsFreshnessReporter(_runs, Options.Create(_options), new Clock(Now)).BuildAsync(Tenant, days);

    private int _n;

    private Task Run(
        TimeSpan ago, CpcdsDropRunStatus status = CpcdsDropRunStatus.Succeeded,
        decimal created = 10, decimal rejected = 0, decimal paidInFile = 100, decimal paidLoaded = 100,
        bool members = false, decimal unmapped = 0, decimal notInNppes = 0, bool claimsFailed = false)
    {
        var started = Now - ago;
        var claimsFile = $"claims_{_n++}.csv";
        var run = new CpcdsDropRun
        {
            Id = "run-" + _n, TenantId = Tenant, StartedAt = started, CompletedAt = started.AddMinutes(5), Status = status,
            Files = [new CpcdsDropRunFile { Name = claimsFile, Kind = CpcdsDropFileKind.Claims, LastWriteUtc = started.AddHours(-1) }],
        };
        if (members)
            run.Steps.Add(new CpcdsDropStep
            {
                Name = "members-and-coverage", Status = CpcdsDropStepStatus.Succeeded,
                Counts = { ["membersCreated"] = 3, ["dependentsCreated"] = 1, ["unmappedPlanCodes"] = unmapped },
            });
        run.Steps.Add(new CpcdsDropStep
        {
            Name = "claim-providers", Files = [claimsFile], Status = CpcdsDropStepStatus.Succeeded,
            Counts = { ["created"] = 2, ["notInNppes"] = notInNppes },
        });
        run.Steps.Add(new CpcdsDropStep
        {
            Name = "claims", Files = [claimsFile],
            Status = claimsFailed ? CpcdsDropStepStatus.Failed : rejected > 0 ? CpcdsDropStepStatus.CompletedWithErrors : CpcdsDropStepStatus.Succeeded,
            Counts = { ["created"] = created, ["rejected"] = rejected, ["paidAmountInFile"] = paidInFile, ["paidAmountLoaded"] = paidLoaded },
        });
        return _runs.SaveAsync(run);
    }

    [Fact]
    public async Task A_daily_extract_loaded_this_morning_is_fresh_and_the_window_adds_up()
    {
        await Run(TimeSpan.FromDays(1), members: true);
        await Run(TimeSpan.FromHours(3), created: 5, rejected: 1, paidInFile: 80, paidLoaded: 60);

        var report = await Report();

        report.Status.Should().Be(CpcdsFreshness.Fresh);
        report.SourceSystem.Should().Be("qnxt");
        report.NewestClaimExtractAt.Should().Be(Now - TimeSpan.FromHours(4));
        report.ClaimExtractAgeHours.Should().Be(4);
        report.LastMembersLoadedAt.Should().Be(Now - TimeSpan.FromDays(1) + TimeSpan.FromMinutes(5));
        report.Window.Runs.Should().Be(2);
        report.Window.ClaimFilesLoaded.Should().Be(2);
        report.Window.ClaimsCreated.Should().Be(15);
        report.Window.ClaimsRejected.Should().Be(1);
        report.Window.PaidAmountInFiles.Should().Be(180);
        report.Window.PaidAmountLoaded.Should().Be(160);
        report.Window.PaidAmountNotLoaded.Should().Be(20);
        report.Window.MembersCreated.Should().Be(4);
        report.Window.ProvidersCreated.Should().Be(4);
        report.Issues.Should().ContainSingle().Which.Should().Contain("1 claims were refused").And.Contain("20.00");
    }

    [Fact]
    public async Task An_extract_older_than_the_expected_interval_plus_grace_is_stale()
    {
        await Run(TimeSpan.FromHours(30));

        var report = await Report();

        report.Status.Should().Be(CpcdsFreshness.Stale);
        report.Issues.Should().Contain(i => i.Contains("31 hours old"));
    }

    [Fact]
    public async Task A_weekly_core_is_judged_against_its_own_interval()
    {
        _options.Tenants[Tenant].ExpectedEvery = TimeSpan.FromDays(7);
        await Run(TimeSpan.FromDays(3));

        (await Report()).Status.Should().Be(CpcdsFreshness.Fresh);
    }

    [Fact]
    public async Task Three_failed_runs_in_a_row_are_failing_even_when_the_last_load_is_recent()
    {
        await Run(TimeSpan.FromHours(10));
        await Run(TimeSpan.FromHours(3), CpcdsDropRunStatus.Failed, claimsFailed: true);
        await Run(TimeSpan.FromHours(2), CpcdsDropRunStatus.Failed, claimsFailed: true);
        await Run(TimeSpan.FromHours(1), CpcdsDropRunStatus.Failed, claimsFailed: true);

        var report = await Report();

        report.Status.Should().Be(CpcdsFreshness.Failing);
        report.ConsecutiveFailedRuns.Should().Be(3);
        report.LastRunStatus.Should().Be(CpcdsDropRunStatus.Failed);
        report.Window.FailedRuns.Should().Be(3);
        report.Window.ClaimFilesLoaded.Should().Be(1, "failed claim steps load nothing");
        report.NewestClaimExtractAt.Should().Be(Now - TimeSpan.FromHours(11));
    }

    [Fact]
    public async Task A_configured_tenant_with_no_claim_load_yet_has_never_loaded_and_an_unconfigured_one_says_so()
    {
        (await Report()).Status.Should().Be(CpcdsFreshness.NeverLoaded);

        _options.Tenants.Clear();
        var report = await Report();
        report.Status.Should().Be(CpcdsFreshness.NotConfigured);
        report.Issues.Should().Contain(i => i.Contains("No CPCDS drop is configured"));
    }

    [Fact]
    public async Task Unmapped_plan_codes_and_providers_missing_from_NPPES_are_issues()
    {
        await Run(TimeSpan.FromHours(2), members: true, unmapped: 2, notInNppes: 3);

        var report = await Report();

        report.Window.UnmappedPlanCodesInLastMemberLoad.Should().Be(2);
        report.Issues.Should().Contain(i => i.StartsWith("2 plan codes"))
            .And.Contain(i => i.StartsWith("3 provider NPIs"));
    }

    [Fact]
    public async Task Runs_before_the_window_count_for_freshness_but_not_for_the_totals()
    {
        await Run(TimeSpan.FromDays(40), created: 99);
        await Run(TimeSpan.FromDays(2), created: 1);

        var report = await Report(days: 7);

        report.Window.Runs.Should().Be(1);
        report.Window.ClaimsCreated.Should().Be(1);
        report.Status.Should().Be(CpcdsFreshness.Stale);
    }
}
