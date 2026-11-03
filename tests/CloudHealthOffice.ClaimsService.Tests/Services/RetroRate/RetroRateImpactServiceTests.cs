using ClaimsService.Controllers;
using ClaimsService.Models;
using ClaimsService.Repositories;
using ClaimsService.Services.Adjudication;
using ClaimsService.Services.Adjudication.Stages;
using ClaimsService.Services.RetroRate;
using CloudHealthOffice.FeeScheduleEngine.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace CloudHealthOffice.ClaimsService.Tests.Services.RetroRate;

public class RetroRateImpactServiceTests
{
    private readonly IClaimRepository _claims = Substitute.For<IClaimRepository>();
    private readonly IFeeScheduleChangeClient _changes = Substitute.For<IFeeScheduleChangeClient>();
    private readonly IShadowAdjudicationPipeline _pipeline = Substitute.For<IShadowAdjudicationPipeline>();
    private readonly IRetroRateImpactRepository _store = Substitute.For<IRetroRateImpactRepository>();
    private IReadOnlyList<RetroRateImpactResult> _saved = [];

    private RetroRateImpactService Service(int maxClaims = 2000)
    {
        _store.SaveRunAsync(Arg.Any<RetroRateImpactRun>(), Arg.Do<IReadOnlyList<RetroRateImpactResult>>(r => _saved = r), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        return new RetroRateImpactService(_claims, _changes, _pipeline, _store,
            Options.Create(new RetroRateOptions { MaxClaimsPerRun = maxClaims }), NullLogger<RetroRateImpactService>.Instance);
    }

    private static ClaimAdjudicationContext Repriced(decimal allowed, decimal paid) => new()
    {
        TenantId = "t1",
        ClaimVersionId = "c-1",
        Claim = new AdapterClaim { Id = "doc-1", ClaimVersionId = "c-1" },
        IsShadow = true,
        MemberLookup = MemberLookupOutcome.Found,
        StageResults = { ClaimAdjudicationStageResult.Pass(BenefitCalculationStage.StageName) },
        AdjudicationResult = new AdjudicationResult { AllowedAmount = allowed, PayerPayment = paid },
    };

    private static Claim Paid(string number, string npi, decimal paid)
    {
        var claim = RetroRateMatcherTests.PaidClaim();
        claim.Id = claim.ClaimVersionId = number;
        claim.ClaimNumber = number;
        claim.BillingProviderNPI = npi;
        claim.AdjudicationResult = new AdjudicationResult { AllowedAmount = paid, PayerPayment = paid };
        return claim;
    }

    [Fact]
    public async Task Run_RepricesMatchedClaims_AndTotalsByDirectionAndProvider()
    {
        var change = RetroRateMatcherTests.Replaced("99213");
        _changes.GetChangeAsync("t1", change.FeeScheduleId, null, Arg.Any<CancellationToken>()).Returns(change);
        var up = Paid("A", "1111111111", 33.95m);
        var down = Paid("B", "2222222222", 50m);
        var same = Paid("C", "1111111111", 35.10m);
        var notTouched = Paid("D", "1111111111", 10m);
        notTouched.ClaimLines = [new ClaimLine { LineNumber = 1, ProcedureCode = "99499", ServiceDateFrom = notTouched.ServiceDateFrom }];
        _claims.FindRetroRateCandidatesAsync(Arg.Any<RetroRateCandidateQuery>(), 2001, Arg.Any<CancellationToken>())
            .Returns([up, down, same, notTouched]);
        _pipeline.EvaluateShadowAsync("t1", Arg.Any<AdapterClaim>(), "u1", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Repriced(35.10m, 35.10m), Repriced(45m, 45m), Repriced(35.10m, 35.10m));

        var run = await Service().RunAsync("t1", new RetroRateImpactRunRequest { FeeScheduleId = change.FeeScheduleId }, "u1", CancellationToken.None);

        Assert.Equal(4, run.CandidatesRead);
        Assert.Equal(3, run.ClaimsAffected);
        Assert.Equal(1, run.CountsByDirection[RetroRateDirection.Underpaid]);
        Assert.Equal(1, run.CountsByDirection[RetroRateDirection.Overpaid]);
        Assert.Equal(1, run.CountsByDirection[RetroRateDirection.Unchanged]);
        Assert.Equal(1.15m, run.TotalUnderpaid);
        Assert.Equal(5m, run.TotalOverpaid);
        Assert.Equal(-3.85m, run.NetPaidDelta);
        Assert.Equal(3, run.CountsByMatch[RetroRateMatch.PlanFallback]);
        Assert.False(run.Truncated);
        Assert.Equal(["2222222222", "1111111111"], run.TopProviders.Select(p => p.BillingProviderNpi));
        Assert.All(_saved, r => Assert.Equal(run.Id, r.RunId));
        Assert.Contains(run.Notes, n => n.Contains("matched by plan"));
        await _pipeline.Received(3).EvaluateShadowAsync("t1", Arg.Any<AdapterClaim>(), "u1", Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Run_PassesTheChangesWindowAndPlans_AndFlagsTruncation()
    {
        var change = RetroRateMatcherTests.Replaced("99213");
        _changes.GetChangeAsync("t1", change.FeeScheduleId, 3, Arg.Any<CancellationToken>()).Returns(change);
        RetroRateCandidateQuery? query = null;
        _claims.FindRetroRateCandidatesAsync(Arg.Do<RetroRateCandidateQuery>(q => query = q), 3, Arg.Any<CancellationToken>())
            .Returns([Paid("A", "1", 1m), Paid("B", "1", 1m), Paid("C", "1", 1m)]);
        _pipeline.EvaluateShadowAsync(default!, default!, default, default, default).ReturnsForAnyArgs(Repriced(1m, 1m));

        var run = await Service(maxClaims: 2).RunAsync("t1",
            new RetroRateImpactRunRequest { FeeScheduleId = change.FeeScheduleId, Revision = 3, Limit = 50 }, "u1", CancellationToken.None);

        Assert.True(run.Truncated);
        Assert.Equal(2, run.CandidatesRead);
        Assert.Equal(change.EffectiveDate, query!.ServiceFrom);
        Assert.Null(query.ServiceBefore);
        Assert.Equal(["STAR-TX"], query.PlanKeys);
    }

    [Fact]
    public async Task Run_RecordsAPendedRepriceAsUndecided()
    {
        var change = RetroRateMatcherTests.Replaced("99213");
        _changes.GetChangeAsync("t1", change.FeeScheduleId, null, Arg.Any<CancellationToken>()).Returns(change);
        _claims.FindRetroRateCandidatesAsync(Arg.Any<RetroRateCandidateQuery>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([Paid("A", "1", 30m)]);
        var pended = Repriced(0m, 0m);
        pended.StageResults.Clear();
        pended.StageResults.Add(ClaimAdjudicationStageResult.Pend(PricingStage.StageName, "no rate"));
        pended.PendDetails = new PendDetails { PendCode = "NOCONTRACT", PendReason = "no rate" };
        _pipeline.EvaluateShadowAsync(default!, default!, default, default, default).ReturnsForAnyArgs(pended);

        var run = await Service().RunAsync("t1", new RetroRateImpactRunRequest { FeeScheduleId = change.FeeScheduleId }, null, CancellationToken.None);

        var result = Assert.Single(_saved);
        Assert.Equal(RetroRateDirection.Undecided, result.Direction);
        Assert.Equal("NOCONTRACT", result.PendCode);
        Assert.Equal(0m, result.NetPaidDelta);
        Assert.Equal(0m, run.NetPaidDelta);
    }

    [Fact]
    public async Task Run_WithNoRecordedChange_Throws()
    {
        _changes.GetChangeAsync("t1", "x", null, Arg.Any<CancellationToken>()).Returns((FeeScheduleChange?)null);

        await Assert.ThrowsAsync<FeeScheduleChangeNotFoundException>(() =>
            Service().RunAsync("t1", new RetroRateImpactRunRequest { FeeScheduleId = "x" }, null, CancellationToken.None));
    }

    [Fact]
    public void Csv_QuotesAndGuardsFormulaCells()
    {
        var csv = RetroRateImpactController.WriteCsv([new RetroRateImpactResult
        {
            ClaimNumber = "=HYPERLINK(1)", ClaimId = "c,1", ClaimStatus = "Paid", Match = RetroRateMatch.Provenance,
            Direction = RetroRateDirection.Overpaid, AffectedLines = [1, 2], NetPaidDelta = -5m, Paid = 50m, Repriced = 45m,
            ServiceDateFrom = new DateTime(2026, 10, 5),
        }]);

        var row = csv.Split(Environment.NewLine)[1];
        Assert.StartsWith("'=HYPERLINK(1),\"c,1\",Paid,2026-10-05", row);
        Assert.Contains(",1 2,", row);
        Assert.Contains(",50.00,45.00,-5.00,", row);
    }

    [Fact]
    public void FeeScheduleChangeClient_PicksTheRequestedOrLatestRevision()
    {
        var changes = new List<FeeScheduleChange> { new() { NewRevision = 1 }, new() { NewRevision = 3 }, new() { NewRevision = 2 } };

        Assert.Equal(3, HttpFeeScheduleChangeClient.Pick(changes, null)!.NewRevision);
        Assert.Equal(2, HttpFeeScheduleChangeClient.Pick(changes, 2)!.NewRevision);
        Assert.Null(HttpFeeScheduleChangeClient.Pick(changes, 9));
    }
}
