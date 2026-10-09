using ClaimsService.Models;
using ClaimsService.Models.Adjudication;
using ClaimsService.Services.Adjudication;
using ClaimsService.Services.Resolution;
using CloudHealthOffice.BenefitEngine.Domain;
using CloudHealthOffice.BenefitEngine.Models;
using NSubstitute;
using Xunit;

namespace CloudHealthOffice.ClaimsService.Tests.Services.Adjudication.Stages;

/// <summary>
/// PR #1278 B2 — BenefitCalculationStage only prices a claim as a later
/// payer when the COB stage cleared it, and never writes accumulators for a
/// claim an earlier stage already pended.
/// </summary>
public partial class BenefitCalculationStageTests
{
    private ClaimAdjudicationContext CobContext(string? sbr01, CobOutcome? cob, params ClaimAdjudicationStageResult[] earlier)
    {
        var claim = BuildClaim(Guid.NewGuid().ToString());
        claim.PayerResponsibilityCode = sbr01;
        claim.OtherPayers = [new ClaimOtherPayer { PayerResponsibilityCode = "P", PayerId = "A", PaidAmount = 40m }];
        var ctx = new ClaimAdjudicationContext
        {
            TenantId = "tenant-1",
            ClaimVersionId = claim.Id,
            Claim = claim,
            PricingResult = PricedAt(claim, 72m),
            ResolvedMember = new ResolvedMember { MemberId = "MEM-1", IsSubscriber = true },
            CobResult = cob,
        };
        ctx.StageResults.AddRange(earlier);
        return ctx;
    }

    [Fact]
    public async Task CobCleared_PricesAsLaterPayer_InProduction()
    {
        BenefitResolutionRequest? captured = null;
        _engine.CalculateAsync(Arg.Do<BenefitResolutionRequest>(r => captured = r), Arg.Any<CancellationToken>())
            .Returns(new BenefitResolutionResult { Success = true, CobPayerSequence = 2 });
        var ctx = CobContext("S", new CobOutcome { ApplyCob = true, PayerSequence = 2, Scenario = CobScenario.ChoSecondaryDetected });

        await _sut.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(2, captured!.Cob!.PayerSequence);
        Assert.Single(captured.Cob.PriorPayers);
        Assert.Equal(AdjudicationExecutionMode.Production, captured.ExecutionMode);
        Assert.Equal(2, ctx.AdjudicationResult.CobPayerSequence);
    }

    /// <summary>
    /// The COB stage pended (e.g. payer-order mismatch) on a claim the 837
    /// sends as a later payer: the claim is not priced at all (never as
    /// primary) and no accumulator is written; approval re-adjudicates it
    /// with the payer order the examiner confirms.
    /// </summary>
    [Fact]
    public async Task CobPended_LaterPayerClaim_IsNotPriced()
    {
        var ctx = CobContext("S",
            new CobOutcome { PendReason = "cob-payer-order-mismatch", Scenario = CobScenario.ChoPrimaryNoSecondary },
            ClaimAdjudicationStageResult.Pend("CoordinationOfBenefits", "mismatch"));

        var result = await _sut.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pend, result.Outcome);
        await _engine.DidNotReceive().CalculateAsync(Arg.Any<BenefitResolutionRequest>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Another stage pended a primary claim (a possible duplicate): the
    /// claim is priced read-only — no accumulator write until an examiner's
    /// approval re-adjudicates it in Production.
    /// </summary>
    [Fact]
    public async Task OtherStagePended_PrimaryClaim_PricesReadOnly()
    {
        BenefitResolutionRequest? captured = null;
        _engine.CalculateAsync(Arg.Do<BenefitResolutionRequest>(r => captured = r), Arg.Any<CancellationToken>())
            .Returns(new BenefitResolutionResult { Success = true });
        var ctx = CobContext("P",
            new CobOutcome { Scenario = CobScenario.ChoPrimaryNoSecondary },
            ClaimAdjudicationStageResult.Pend("DuplicateClaim", "possible duplicate"));

        await _sut.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Null(captured!.Cob);
        Assert.Equal(AdjudicationExecutionMode.Prospective, captured.ExecutionMode);
    }

    /// <summary>
    /// Re-review N4: coverage says secondary and the COB stage passed
    /// without clearing COB (e.g. a mode that let it through): never priced
    /// as primary.
    /// </summary>
    [Fact]
    public async Task CoverageSaysSecondary_CobNotCleared_Pends()
    {
        var ctx = CobContext("P", new CobOutcome { Scenario = CobScenario.ChoSecondaryDetected, ApplyCob = false });

        var result = await _sut.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pend, result.Outcome);
        await _engine.DidNotReceive().CalculateAsync(Arg.Any<BenefitResolutionRequest>(), Arg.Any<CancellationToken>());
    }

    /// <summary>An examiner confirmed this plan is primary: priced as primary despite SBR01 S.</summary>
    [Fact]
    public async Task ExaminerConfirmedPrimary_PricesAsPrimary()
    {
        BenefitResolutionRequest? captured = null;
        _engine.CalculateAsync(Arg.Do<BenefitResolutionRequest>(r => captured = r), Arg.Any<CancellationToken>())
            .Returns(new BenefitResolutionResult { Success = true });
        var ctx = CobContext("S", new CobOutcome { ConfirmedByExaminer = true, PayerSequence = 1 });

        await _sut.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Null(captured!.Cob);
        Assert.Equal(AdjudicationExecutionMode.Production, captured.ExecutionMode);
    }

    /// <summary>
    /// Re-review minor: DeductibleCredited only when COB was applied — a
    /// primary claim leaves it null (an older benefit-plan-service that sends
    /// no credit field cannot zero out a primary claim's deductible credit).
    /// </summary>
    [Fact]
    public async Task PrimaryClaim_LeavesDeductibleCreditedNull()
    {
        _engine.CalculateAsync(Arg.Any<BenefitResolutionRequest>(), Arg.Any<CancellationToken>())
            .Returns(new BenefitResolutionResult
            {
                Success = true,
                Totals = new ClaimTotals { TotalDeductible = 50m, TotalDeductibleCredited = 0m },
                Lines = [new LineBenefitResult { LineNumber = 1, IsCovered = true, DeductibleAmount = 50m, DeductibleCreditedAmount = 0m }],
            });
        var ctx = CobContext("P", new CobOutcome { Scenario = CobScenario.ChoPrimaryNoSecondary });

        await _sut.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Null(ctx.AdjudicationResult.DeductibleCreditedAmount);
        Assert.All(ctx.LineAdjudicationResults, l => Assert.Null(l.DeductibleCreditedAmount));
    }

    [Fact]
    public async Task Replacement_SendsTheReplacedClaimId()
    {
        BenefitResolutionRequest? captured = null;
        _engine.CalculateAsync(Arg.Do<BenefitResolutionRequest>(r => captured = r), Arg.Any<CancellationToken>())
            .Returns(new BenefitResolutionResult { Success = true });
        var ctx = CobContext("P", new CobOutcome { Scenario = CobScenario.ChoPrimaryNoSecondary });
        ctx.Claim.PredecessorVersionId = "claim-original";

        await _sut.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal("claim-original", captured!.ReplacesClaimId);
    }

    /// <summary>
    /// The 837 says secondary but the COB stage never ran (disabled): never
    /// price a later-payer claim as primary — pend.
    /// </summary>
    [Fact]
    public async Task LaterPayerClaim_WithoutCobDetermination_Pends()
    {
        var ctx = CobContext("S", cob: null);

        var result = await _sut.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pend, result.Outcome);
        Assert.Equal("COB", ctx.PendDetails!.PendCode);
        await _engine.DidNotReceive().CalculateAsync(Arg.Any<BenefitResolutionRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PrimaryClaim_CobStagePassedWithoutCob_NoCob()
    {
        BenefitResolutionRequest? captured = null;
        _engine.CalculateAsync(Arg.Do<BenefitResolutionRequest>(r => captured = r), Arg.Any<CancellationToken>())
            .Returns(new BenefitResolutionResult { Success = true });
        var ctx = CobContext("P", new CobOutcome { Scenario = CobScenario.ChoPrimaryNoSecondary });

        await _sut.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Null(captured!.Cob);
        Assert.Equal(AdjudicationExecutionMode.Production, captured.ExecutionMode);
    }
}
