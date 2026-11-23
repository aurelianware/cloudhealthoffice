using System.Text;
using ClaimsService.Controllers;
using ClaimsService.Services.Adjudication.Shadow;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace CloudHealthOffice.ClaimsService.Tests.Services.Adjudication.Shadow;

public class ShadowAppealsTests
{
    private static readonly DateTime Now = new(2026, 11, 23, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(ShadowOutcome.Denied, "50", ShadowOutcome.Paid, null, ShadowAppealAdvice.ChoWouldPay)]
    [InlineData(ShadowOutcome.Denied, "50", ShadowOutcome.Denied, "50", ShadowAppealAdvice.ChoConfirms)]
    [InlineData(ShadowOutcome.Denied, "50", ShadowOutcome.Denied, "197", ShadowAppealAdvice.ChoDeniesForAnotherReason)]
    public void ACoreDenial_IsReadAgainstChosAnswer(string core, string coreCode, string cho, string? choCode, string expected)
    {
        var r = Result("C-1", ShadowDiscrepancyKind.OutcomeMismatch, core, cho, coreCode, choCode);
        Assert.Equal(expected, ShadowAppealAdvice.For(r));
    }

    [Fact]
    public void APaidClaim_WithALineTheCoreDeniedAndChoWouldPay_IsALikelyOverturn()
    {
        var r = Result("C-1", ShadowDiscrepancyKind.PaidVariance, ShadowOutcome.Paid, ShadowOutcome.Paid);
        r.Lines.Add(new ShadowLineComparison { LineNumber = 2, CoreDenialCode = "197", ChoPaid = 40m });

        var advice = ShadowAppealAdvice.For(r);

        Assert.Equal(ShadowAppealAdvice.ChoWouldPay, advice);
        Assert.True(ShadowAppealAdvice.LikelyOverturn(advice));
    }

    [Fact]
    public void UndecidedAndNotComparable_AreNeverLikelyOverturns()
    {
        Assert.Equal(ShadowAppealAdvice.ChoUndecided,
            ShadowAppealAdvice.For(Result("C-1", ShadowDiscrepancyKind.ChoUndecided, ShadowOutcome.Denied, ShadowOutcome.Pended, "50")));
        Assert.Equal(ShadowAppealAdvice.NotComparable,
            ShadowAppealAdvice.For(Result("C-1", ShadowDiscrepancyKind.NotComparable, ShadowOutcome.NotFinal, ShadowOutcome.Paid)));
        Assert.False(ShadowAppealAdvice.LikelyOverturn(ShadowAppealAdvice.ChoUndecided));
    }

    [Fact]
    public void TheOutcomesFile_ReadsTheUsualSpellings_AndRefusesRowsItCannotRead()
    {
        const string csv = """
            Claim number,Appeal number,Decision,Decision date,CARC
            C-1,A-1,Reversed,2026-06-01,50
            C-2,,upheld,06/02/2026,50
            C-3,A-3,partially overturned,,197
            C-4,A-4,maybe,2026-06-01,50
            ,A-5,upheld,2026-06-01,50
            C-1,A-1,overturned,2026-06-03,50
            """;

        var (outcomes, result) = ShadowAppealOutcomeFile.Parse("tenant-1", csv, "ops-1", Now);

        Assert.Empty(result.FileErrors);
        Assert.Equal(6, result.Rows);
        Assert.Equal(2, result.Rejected);
        Assert.Equal(3, result.Loaded);
        Assert.Contains(result.RowErrors, e => e.StartsWith("Line 5:") && e.Contains("maybe"));
        var c1 = outcomes.Single(o => o.ClaimNumber == "C-1");
        Assert.Equal(ShadowAppealDecision.Overturned, c1.Decision);
        Assert.Equal(new DateTime(2026, 6, 3), c1.DecidedDate);
        Assert.Equal("tenant-1:C-1:A-1", c1.Id);
        Assert.Equal("1", outcomes.Single(o => o.ClaimNumber == "C-2").AppealNumber);
        Assert.Equal(ShadowAppealDecision.PartiallyOverturned, outcomes.Single(o => o.ClaimNumber == "C-3").Decision);
    }

    [Fact]
    public void AFileWithoutADecisionColumn_IsRefused()
    {
        var (_, result) = ShadowAppealOutcomeFile.Parse("tenant-1", "Claim number,Date\nC-1,2026-01-01\n", null, Now);
        Assert.Contains("The file has no \"Decision\" column.", result.FileErrors);
    }

    [Fact]
    public void TheSummary_ShowsHowOftenAppealsOfDenialsChoWouldPay_WereOverturned()
    {
        var outcomes = new[]
        {
            Outcome("C-1", ShadowAppealDecision.Overturned, "50"),
            Outcome("C-2", ShadowAppealDecision.Overturned, "50"),
            Outcome("C-3", ShadowAppealDecision.Upheld, "50"),
            Outcome("C-4", ShadowAppealDecision.Upheld, "50"),
            Outcome("C-5", ShadowAppealDecision.PartiallyOverturned, null),
            Outcome("C-6", ShadowAppealDecision.Withdrawn, "50"),
            Outcome("C-7", ShadowAppealDecision.Upheld, "16"),
        };
        var latest = new Dictionary<string, ShadowAdjudicationResult>
        {
            ["C-1"] = Result("C-1", ShadowDiscrepancyKind.OutcomeMismatch, ShadowOutcome.Denied, ShadowOutcome.Paid, "50"),
            ["C-2"] = Result("C-2", ShadowDiscrepancyKind.OutcomeMismatch, ShadowOutcome.Denied, ShadowOutcome.Paid, "50"),
            ["C-3"] = Result("C-3", ShadowDiscrepancyKind.Match, ShadowOutcome.Denied, ShadowOutcome.Denied, "50", "50"),
            ["C-4"] = Result("C-4", ShadowDiscrepancyKind.OutcomeMismatch, ShadowOutcome.Denied, ShadowOutcome.Paid, "50"),
            ["C-5"] = Result("C-5", ShadowDiscrepancyKind.Match, ShadowOutcome.Denied, ShadowOutcome.Denied, "197", "197"),
        };

        var summary = ShadowAppealSummaryBuilder.Build(outcomes, latest, 0.01m, Now);

        Assert.Equal(6, summary.Decided);
        Assert.Equal(3, summary.Overturned);
        Assert.Equal(0.5m, summary.OverturnRate);
        Assert.Equal(1, summary.NotShadowed);

        var pay = summary.ByAdvice.Single(r => r.Key == ShadowAppealAdvice.ChoWouldPay);
        Assert.Equal(3, pay.Decided);
        Assert.Equal(2, pay.Overturned);
        Assert.Equal(0.6667m, pay.OverturnRate);
        var confirms = summary.ByAdvice.Single(r => r.Key == ShadowAppealAdvice.ChoConfirms);
        Assert.Equal(2, confirms.Decided);
        Assert.Equal(1, confirms.Overturned);

        // C-5's CARC comes from its shadow result when the file gives none.
        Assert.Equal(["50", "16", "197"], summary.ByDenialCode.Select(r => r.Key).ToList());
        var carc50 = summary.ByDenialCode[0];
        Assert.Equal(4, carc50.Decided);
        Assert.Equal(3, carc50.ChoWouldPay);
        Assert.Equal(2, carc50.ChoWouldPayOverturned);
        Assert.Equal(1, carc50.ChoConfirms);
        Assert.Equal(0, carc50.ChoConfirmsOverturned);
    }

    [Fact]
    public async Task TheEvidence_IsTheClaimsLatestResult_WithTheCarcsAppealHistory()
    {
        var results = new InMemoryShadowAdjudicationRepository();
        var older = Result("C-1", ShadowDiscrepancyKind.ChoUndecided, ShadowOutcome.Denied, ShadowOutcome.Pended, "50");
        older.CreatedAtUtc = Now.AddDays(-30);
        var newer = Result("C-1", ShadowDiscrepancyKind.OutcomeMismatch, ShadowOutcome.Denied, ShadowOutcome.Paid, "50");
        newer.CreatedAtUtc = Now.AddDays(-1);
        await results.SaveRunAsync(new ShadowAdjudicationRun { TenantId = "tenant-1" }, [older, newer]);

        var outcomes = new InMemoryShadowAppealOutcomeRepository();
        var service = Service(results, outcomes);
        var history = Enumerable.Range(1, 10)
            .Select(i => Outcome($"H-{i}", i <= 7 ? ShadowAppealDecision.Overturned : ShadowAppealDecision.Upheld, "50"))
            .ToList();
        await outcomes.SaveManyAsync(history);

        var evidence = await service.EvidenceAsync("tenant-1", "C-1");

        Assert.NotNull(evidence);
        Assert.Equal(ShadowAppealAdvice.ChoWouldPay, evidence!.Advice);
        Assert.True(evidence.LikelyOverturn);
        Assert.Same(newer, evidence.Result);
        Assert.Equal(10, evidence.DenialCodeHistory!.Decided);
        Assert.Equal(0.7m, evidence.DenialCodeHistory.OverturnRate);

        Assert.Null(await service.EvidenceAsync("tenant-1", "C-unknown"));
        Assert.Null(await service.EvidenceAsync("tenant-2", "C-1"));
    }

    [Fact]
    public async Task FewerThanTenEarlierAppeals_ShowNoCarcRate()
    {
        var results = new InMemoryShadowAdjudicationRepository();
        await results.SaveRunAsync(new ShadowAdjudicationRun { TenantId = "tenant-1" },
            [Result("C-1", ShadowDiscrepancyKind.OutcomeMismatch, ShadowOutcome.Denied, ShadowOutcome.Paid, "50")]);
        var outcomes = new InMemoryShadowAppealOutcomeRepository();
        await outcomes.SaveManyAsync([Outcome("H-1", ShadowAppealDecision.Overturned, "50")]);

        var evidence = await Service(results, outcomes).EvidenceAsync("tenant-1", "C-1");

        Assert.Null(evidence!.DenialCodeHistory);
    }

    [Fact]
    public async Task TheController_ImportsAFile_AndRefusesOneItCannotRead()
    {
        var outcomes = new InMemoryShadowAppealOutcomeRepository();
        var controller = new ShadowAppealsController(Service(new InMemoryShadowAdjudicationRepository(), outcomes), Actor());
        var http = new DefaultHttpContext();
        http.Items["TenantId"] = "tenant-1";
        controller.ControllerContext = new ControllerContext { HttpContext = http };

        var ok = Assert.IsType<OkObjectResult>((await controller.Import(File("Claim number,Decision\nC-1,overturned\n"))).Result);
        Assert.Equal(1, Assert.IsType<ShadowAppealOutcomeImportResult>(ok.Value).Loaded);
        Assert.Single(await outcomes.ListAsync("tenant-1", null, 10));

        Assert.IsType<UnprocessableEntityObjectResult>((await controller.Import(File("Claim number\nC-1\n"))).Result);
        Assert.IsType<NotFoundResult>((await controller.Evidence("C-404")).Result);

        var dry = Assert.IsType<OkObjectResult>((await controller.Import(File("Claim number,Decision\nC-2,upheld\n"), dryRun: true)).Result);
        Assert.True(Assert.IsType<ShadowAppealOutcomeImportResult>(dry.Value).DryRun);
        Assert.Single(await outcomes.ListAsync("tenant-1", null, 10));
    }

    private static ShadowAppealService Service(IShadowAdjudicationRepository results, IShadowAppealOutcomeRepository outcomes) =>
        new(results, outcomes, Options.Create(new ShadowAdjudicationOptions()), new FixedClock(Now));

    private static ICurrentActor Actor()
    {
        var actor = Substitute.For<ICurrentActor>();
        actor.TenantId.Returns("tenant-1");
        actor.UserId.Returns("ops-1");
        return actor;
    }

    private static IFormFile File(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        return new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", "outcomes.csv");
    }

    private static ShadowAdjudicationResult Result(
        string claimNumber, string kind, string core, string cho, string? coreCode = null, string? choCode = null) => new()
    {
        TenantId = "tenant-1",
        ClaimId = "chain-" + claimNumber,
        ClaimNumber = claimNumber,
        Kind = kind,
        CoreOutcome = core,
        ChoOutcome = cho,
        CoreDenialCode = coreCode,
        ChoDenialCode = choCode,
        CreatedAtUtc = Now.AddDays(-1),
    };

    private static ShadowAppealOutcome Outcome(string claimNumber, string decision, string? carc) => new()
    {
        Id = ShadowAppealOutcome.IdFor("tenant-1", claimNumber, "1"),
        TenantId = "tenant-1",
        ClaimNumber = claimNumber,
        AppealNumber = "1",
        Decision = decision,
        DenialCode = carc,
        DecidedDate = Now.AddDays(-5),
    };

    private sealed class FixedClock(DateTime now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(now, TimeSpan.Zero);
    }
}
