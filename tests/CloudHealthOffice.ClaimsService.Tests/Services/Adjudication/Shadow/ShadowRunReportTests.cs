using ClaimsService.Controllers;
using ClaimsService.Services.Adjudication.Shadow;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using Xunit;

namespace CloudHealthOffice.ClaimsService.Tests.Services.Adjudication.Shadow;

public class ShadowRunReportTests
{
    [Fact]
    public void TheReport_SplitsUndecidedFromDecided_AndMoneyByDirection()
    {
        var report = ShadowRunReportBuilder.Build(new ShadowAdjudicationRun(), Results());

        Assert.Equal(7, report.ComparableClaims);
        Assert.Equal(4, report.DecidedClaims);
        // Core paid more than CHO would have: C2 (-30) and C4 (-60).
        Assert.Equal(2, report.CorePaidMore.Claims);
        Assert.Equal(90m, report.CorePaidMore.Amount);
        // Core paid less: C3 (+15).
        Assert.Equal(1, report.CorePaidLess.Claims);
        Assert.Equal(15m, report.CorePaidLess.Amount);
        Assert.Equal(ShadowDiscrepancyKind.All, report.ByKind.Select(r => r.Key));
        Assert.Equal(1, report.ByKind.Single(r => r.Key == ShadowDiscrepancyKind.NotComparable).Claims);
    }

    [Fact]
    public void UndecidedGaps_AreRankedByClaims_WithTheMoneyCHOCannotCheckYet()
    {
        var report = ShadowRunReportBuilder.Build(new ShadowAdjudicationRun(), Results());

        var first = report.UndecidedByGap[0];
        Assert.Equal("NOCONTRACT", first.Key);
        Assert.Equal(2, first.Claims);
        Assert.Equal(140m, first.CorePaid);
        Assert.Equal("MEMBERNOTLOADED", report.UndecidedByGap[1].Key);
    }

    [Fact]
    public void StagesDenialsProceduresAndProviders_AreRankedByDollars()
    {
        var report = ShadowRunReportBuilder.Build(new ShadowAdjudicationRun(), Results());

        // C4 (TimelyFiling, 60) outranks C2 (no finding, benefit difference, 30) and C3 (pricing, 15).
        Assert.Equal(["TimelyFiling", "BenefitCalculation", "Pricing"], report.ByChoStage.Select(r => r.Key));
        Assert.Equal("paid → 29", Assert.Single(report.ByDenialCodes).Key);
        Assert.Equal("99214", report.ByProcedure[0].Key);
        Assert.Equal(1, report.ByProcedure[0].Lines);
        Assert.Equal(-30m, report.ByProcedure[0].NetPaidVariance);
        Assert.Equal("2222222222", report.ByBillingProvider[0].Key);
        Assert.Equal(75m, report.ByBillingProvider[0].AbsolutePaidVariance);
        Assert.Equal(["C4", "C3"], report.ByBillingProvider[0].ExampleClaims);
    }

    [Fact]
    public void TopN_LimitsEveryBreakdown()
    {
        var report = ShadowRunReportBuilder.Build(new ShadowAdjudicationRun(), Results(), topN: 1);

        Assert.Single(report.ByChoStage);
        Assert.Single(report.UndecidedByGap);
        Assert.Equal(ShadowDiscrepancyKind.All.Count, report.ByKind.Count);
    }

    [Fact]
    public void TheCsv_HasOneRowPerDifferingLine_AndLeavesOutMatches()
    {
        var csv = ShadowDiscrepancyCsv.Write(Results(), includeUndecided: false);
        var rows = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(string.Join(",", ShadowDiscrepancyCsv.Header), rows[0]);
        Assert.Equal(4, rows.Length); // header + C2 line 2 + C3 line 1 + C4 claim row
        Assert.Contains(rows, r => r.StartsWith("C2,") && r.Contains(",2,99214,") && r.Contains(",-30.00,"));
        Assert.Contains(rows, r => r.StartsWith("C4,") && r.Contains("OutcomeMismatch") && r.Contains("TimelyFiling: Filed late"));
        Assert.DoesNotContain(rows, r => r.StartsWith("C1,") || r.StartsWith("C5,") || r.StartsWith("C8,"));
    }

    [Fact]
    public void TheCsv_AddsUndecidedClaims_OnRequest_WithoutChoAmounts()
    {
        var rows = ShadowDiscrepancyCsv.Write(Results(), includeUndecided: true)
            .Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        var c5 = Assert.Single(rows, r => r.StartsWith("C5,"));
        Assert.Contains(",NOCONTRACT,", c5);
        Assert.Contains(",80.00,,60.00,,,", c5);
    }

    [Theory]
    [InlineData("=HYPERLINK(\"x\")", "\"'=HYPERLINK(\"\"x\"\")\"")]
    [InlineData("+1", "'+1")]
    [InlineData("@SUM", "'@SUM")]
    [InlineData("-cmd", "'-cmd")]
    [InlineData("-30.00", "-30.00")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData(null, "")]
    public void CsvFields_AreQuoted_AndFormulasNeutralised(string? value, string expected) =>
        Assert.Equal(expected, ShadowDiscrepancyCsv.Escape(value));

    [Fact]
    public async Task TheEndpoints_Are404_ForAnotherTenantsOrUnknownRun()
    {
        var store = Substitute.For<IShadowAdjudicationRepository>();
        var controller = Controller(store);

        Assert.IsType<NotFoundResult>((await controller.Report("missing")).Result);
        Assert.IsType<NotFoundResult>(await controller.DiscrepanciesCsv("missing"));
        await store.DidNotReceiveWithAnyArgs().ListResultsAsync(default!, default!, default, default, default);
    }

    [Fact]
    public async Task TheCsvEndpoint_ReturnsAFileNamedForTheRun()
    {
        var store = Substitute.For<IShadowAdjudicationRepository>();
        store.GetRunAsync("tenant-1", "run-1", Arg.Any<CancellationToken>())
            .Returns(new ShadowAdjudicationRun { Id = "run-1", TenantId = "tenant-1" });
        store.ListResultsAsync("tenant-1", "run-1", null, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Results());

        var file = Assert.IsType<FileContentResult>(await Controller(store).DiscrepanciesCsv("run-1"));

        Assert.Equal("shadow-adjudication-run-1.csv", file.FileDownloadName);
        Assert.StartsWith("text/csv", file.ContentType);
        Assert.StartsWith("ClaimNumber,", System.Text.Encoding.UTF8.GetString(file.FileContents));
    }

    private static ShadowAdjudicationController Controller(IShadowAdjudicationRepository store)
    {
        var controller = new ShadowAdjudicationController(
            Substitute.For<IShadowAdjudicationService>(), store,
            new ShadowAdjudicationTenants(Microsoft.Extensions.Options.Options.Create(new ShadowAdjudicationOptions()),
                new InMemoryShadowAdjudicationTenantSettingsRepository()),
            Substitute.For<ICurrentActor>());
        var http = new DefaultHttpContext();
        http.Items["TenantId"] = "tenant-1";
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        return controller;
    }

    /// <summary>
    /// C1 match; C2 paid less by CHO on line 2 (benefits); C3 priced higher by CHO;
    /// C4 CHO denies (timely filing); C5/C6 undecided NOCONTRACT; C7 member not loaded;
    /// C8 not comparable.
    /// </summary>
    private static List<ShadowAdjudicationResult> Results() =>
    [
        Decided("C1", ShadowDiscrepancyKind.Match, "1111111111", 0m),
        Decided("C2", ShadowDiscrepancyKind.PaidVariance, "1111111111", -30m, line: new ShadowLineComparison
        {
            LineNumber = 2, ProcedureCode = "99214", Charge = 150m, CoreAmountsPresent = true,
            CoreAllowed = 100m, ChoAllowed = 100m, CorePaid = 80m, ChoPaid = 50m,
            Differences = [ShadowLineDifference.Paid],
        }),
        Decided("C3", ShadowDiscrepancyKind.AllowedVariance, "2222222222", 15m, line: new ShadowLineComparison
        {
            LineNumber = 1, ProcedureCode = "99213", Charge = 120m, CoreAmountsPresent = true,
            CoreAllowed = 80m, ChoAllowed = 100m, CorePaid = 60m, ChoPaid = 75m,
            Differences = [ShadowLineDifference.Allowed, ShadowLineDifference.Paid],
        }),
        Decided("C4", ShadowDiscrepancyKind.OutcomeMismatch, "2222222222", -60m, choDenial: "29",
            finding: "TimelyFiling: Filed late"),
        Undecided("C5", "NOCONTRACT", 60m),
        Undecided("C6", "NOCONTRACT", 80m),
        Undecided("C7", "MEMBERNOTLOADED", 300m),
        new() { Id = "r8", ClaimNumber = "C8", Kind = ShadowDiscrepancyKind.NotComparable },
    ];

    private static ShadowAdjudicationResult Decided(
        string claim, string kind, string npi, decimal variance,
        ShadowLineComparison? line = null, string? choDenial = null, string? finding = null) => new()
    {
        Id = "r-" + claim,
        ClaimNumber = claim,
        Kind = kind,
        BillingProviderNpi = npi,
        CoreOutcome = ShadowOutcome.Paid,
        ChoOutcome = choDenial is null ? ShadowOutcome.Paid : ShadowOutcome.Denied,
        CoreAllowed = 80m,
        CorePaid = 60m,
        ChoAllowed = 80m,
        ChoPaid = 60m + variance,
        PaidVariance = variance,
        AbsolutePaidVariance = Math.Abs(variance),
        ChoDenialCode = choDenial,
        ChoStageFindings = finding is null ? [] : [finding],
        Lines = line is null ? [] : [line],
    };

    private static ShadowAdjudicationResult Undecided(string claim, string gap, decimal corePaid) => new()
    {
        Id = "r-" + claim,
        ClaimNumber = claim,
        Kind = ShadowDiscrepancyKind.ChoUndecided,
        BillingProviderNpi = "3333333333",
        CoreOutcome = ShadowOutcome.Paid,
        ChoOutcome = ShadowOutcome.Pended,
        CoreAllowed = 80m,
        CorePaid = corePaid,
        ChoPendCode = gap,
    };
}
