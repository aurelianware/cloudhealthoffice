using ClaimsService.Models;
using ClaimsService.Repositories;
using ClaimsService.Services.Adjudication;
using ClaimsService.Services.Adjudication.Shadow;
using ClaimsService.Services.Adjudication.Stages;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace CloudHealthOffice.ClaimsService.Tests.Services.Adjudication.Shadow;

public class ShadowAdjudicationServiceTests
{
    private readonly IClaimRepository _claims = Substitute.For<IClaimRepository>();
    private readonly InMemoryClaimImportTransactionRepository _transactions = new();
    private readonly IShadowAdjudicationPipeline _pipeline = Substitute.For<IShadowAdjudicationPipeline>();
    private readonly IShadowAdjudicationRepository _store = Substitute.For<IShadowAdjudicationRepository>();
    private readonly ShadowAdjudicationOptions _options = new() { Enabled = true };

    public ShadowAdjudicationServiceTests()
    {
        _pipeline.EvaluateShadowAsync(Arg.Any<string>(), Arg.Any<AdapterClaim>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ci => Passed(ci.ArgAt<AdapterClaim>(1), allowed: 80m, paid: 60m, liability: 20m));
    }

    [Fact]
    public async Task ARunIsRefused_UnlessShadowAdjudicationIsEnabledForTheTenant()
    {
        _options.Enabled = false;
        await Assert.ThrowsAsync<ShadowAdjudicationDisabledException>(() => Run(new() { ClaimIds = ["cpcds-1"] }));

        _options.Enabled = true;
        _options.Tenants.Add("another-tenant");
        await Assert.ThrowsAsync<ShadowAdjudicationDisabledException>(() => Run(new() { ClaimIds = ["cpcds-1"] }));
    }

    [Fact]
    public async Task ImportedClaims_AreComparedAndStored_OthersAreSkipped()
    {
        Store(Imported("cpcds-1"));
        var native = ShadowDiscrepancyComparerTests.CorePaid(80m, 60m, 20m);
        native.ClaimVersionId = "cho-native";
        native.ExternalSource = null;
        Store(native);

        var response = await Run(new() { ClaimIds = ["cpcds-1", "cho-native", "missing"] });

        var result = Assert.Single(response.Results);
        Assert.Equal(ShadowDiscrepancyKind.Match, result.Kind);
        Assert.Equal(response.Run.Id, result.RunId);
        Assert.Equal(2, response.Run.Skipped.Count);
        Assert.Equal(1m, response.Run.MatchRate);
        await _store.Received(1).SaveRunAsync(response.Run, Arg.Is<IReadOnlyList<ShadowAdjudicationResult>>(r => r.Count == 1), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TheClaimTheStoreHolds_IsNeverHandedToThePipeline()
    {
        var core = Imported("cpcds-1");
        Store(core);
        AdapterClaim? seen = null;
        _pipeline.EvaluateShadowAsync(Arg.Any<string>(), Arg.Any<AdapterClaim>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                seen = ci.ArgAt<AdapterClaim>(1);
                seen.BenefitPlanId = "resolved-by-the-pipeline";
                seen.ClaimLines[0].ChargeAmount = 1m;
                return Passed(seen, 80m, 60m, 20m);
            });

        await Run(new() { ClaimIds = ["cpcds-1"] });

        Assert.NotNull(seen);
        Assert.Null(seen!.AdjudicationResult);
        Assert.Null(seen.ClaimLines[0].AdjudicationResult);
        Assert.Equal(ClaimStatus.Submitted, seen.Status);
        Assert.Equal(ClaimStatus.Paid, core.Status);
        Assert.Equal(100m, core.ClaimLines[0].ChargeAmount);
        Assert.Null(core.BenefitPlanId);
        Assert.Equal(60m, core.AdjudicationResult!.PayerPayment);
        await _claims.DidNotReceiveWithAnyArgs().UpdateAsync(default!);
    }

    [Fact]
    public async Task ABatchRun_PicksTheClaimsOfThatImportBatch_UpToTheLimit()
    {
        foreach (var id in new[] { "cpcds-1", "cpcds-2", "cpcds-3" })
        {
            Store(Imported(id));
            await _transactions.CreateAsync(new ClaimImportTransaction
            {
                TenantId = "tenant-1", ImportBatchId = "batch-1", ClaimId = id, Format = "cpcds",
            });
        }
        await _transactions.CreateAsync(new ClaimImportTransaction
        {
            TenantId = "tenant-1", ImportBatchId = "batch-1", ClaimId = null, Outcome = "Rejected",
        });
        await _transactions.CreateAsync(new ClaimImportTransaction
        {
            TenantId = "tenant-1", ImportBatchId = "batch-2", ClaimId = "cpcds-9",
        });

        var response = await Run(new() { ImportBatchId = "batch-1", Limit = 2 });

        Assert.Equal(2, response.Run.ClaimsRequested);
        Assert.Equal("batch-1", response.Run.ImportBatchId);
        Assert.All(response.Results, r => Assert.NotEqual("cpcds-9", r.ClaimId));
    }

    [Fact]
    public async Task AFailingClaim_IsRecorded_AndTheRunCarriesOn()
    {
        Store(Imported("cpcds-1"));
        Store(Imported("cpcds-2"));
        _pipeline.EvaluateShadowAsync(Arg.Any<string>(), Arg.Is<AdapterClaim>(c => c.ClaimVersionId == "cpcds-1"),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns<ClaimAdjudicationContext>(_ => throw new InvalidOperationException("boom"));

        var response = await Run(new() { ClaimIds = ["cpcds-1", "cpcds-2"] });

        Assert.Equal(["cpcds-1: InvalidOperationException"], response.Run.Failed);
        Assert.Single(response.Results);
    }

    [Fact]
    public async Task ANotFinalCoreClaim_IsNotRunThroughThePipeline()
    {
        var pended = Imported("cpcds-1");
        pended.Status = ClaimStatus.Pended;
        Store(pended);

        var response = await Run(new() { ClaimIds = ["cpcds-1"] });

        Assert.Equal(ShadowDiscrepancyKind.NotComparable, Assert.Single(response.Results).Kind);
        await _pipeline.DidNotReceiveWithAnyArgs().EvaluateShadowAsync(default!, default!, default, default, default);
        Assert.Null(response.Run.MatchRate);
    }

    [Fact]
    public void TheRunTotals_SeparateUndecidedFromDecided()
    {
        var run = new ShadowAdjudicationRun();
        ShadowAdjudicationService.Summarize(run,
        [
            new() { Kind = ShadowDiscrepancyKind.Match },
            new() { Kind = ShadowDiscrepancyKind.PaidVariance, PaidVariance = -30m, AbsolutePaidVariance = 30m },
            new() { Kind = ShadowDiscrepancyKind.AllowedVariance, PaidVariance = 10m, AbsolutePaidVariance = 10m },
            new() { Kind = ShadowDiscrepancyKind.Match },
            new() { Kind = ShadowDiscrepancyKind.ChoUndecided },
            new() { Kind = ShadowDiscrepancyKind.NotComparable },
        ]);

        Assert.Equal(6, run.ClaimsCompared);
        Assert.Equal(0.5m, run.MatchRate);
        Assert.Equal(0.2m, run.UndecidedRate);
        Assert.Equal(-20m, run.NetPaidVariance);
        Assert.Equal(40m, run.AbsolutePaidVariance);
        Assert.Equal(2, run.CountsByKind[ShadowDiscrepancyKind.Match]);
        Assert.Equal(0, run.CountsByKind[ShadowDiscrepancyKind.OutcomeMismatch]);
    }

    private Task<ShadowAdjudicationRunResponse> Run(ShadowAdjudicationRunRequest request) =>
        new ShadowAdjudicationService(
                _claims, _transactions, _pipeline, _store, Options.Create(_options),
                NullLogger<ShadowAdjudicationService>.Instance)
            .RunAsync("tenant-1", request, "actor-1", CancellationToken.None);

    private void Store(Claim claim) =>
        _claims.GetLatestVersionAsync(claim.ClaimVersionId, Arg.Any<DateTime>()).Returns(claim);

    private static Claim Imported(string chainId)
    {
        var claim = ShadowDiscrepancyComparerTests.CorePaid(80m, 60m, 20m);
        claim.ClaimVersionId = chainId;
        claim.Id = chainId + "-v1";
        return claim;
    }

    private static ClaimAdjudicationContext Passed(AdapterClaim claim, decimal allowed, decimal paid, decimal liability)
    {
        var context = new ClaimAdjudicationContext
        {
            TenantId = "tenant-1",
            ClaimVersionId = claim.ClaimVersionId,
            Claim = claim,
            IsShadow = true,
            MemberLookup = MemberLookupOutcome.Found,
            AdjudicationResult = new AdjudicationResult
            {
                AllowedAmount = allowed, PayerPayment = paid, PatientResponsibility = liability,
            },
            LineAdjudicationResults =
            [
                new LineAdjudicationResult { AllowedAmount = allowed, PaidAmount = paid, PatientResponsibility = liability },
            ],
        };
        context.StageResults.Add(ClaimAdjudicationStageResult.Pass(BenefitCalculationStage.StageName));
        return context;
    }
}
