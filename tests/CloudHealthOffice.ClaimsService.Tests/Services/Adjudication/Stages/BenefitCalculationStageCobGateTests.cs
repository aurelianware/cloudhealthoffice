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
    /// The COB stage pended (e.g. payer-order mismatch): the claim is
    /// priced read-only with no COB — no accumulator write for a claim that
    /// will not finalize now.
    /// </summary>
    [Fact]
    public async Task CobPended_PricesReadOnly_NoCob()
    {
        BenefitResolutionRequest? captured = null;
        _engine.CalculateAsync(Arg.Do<BenefitResolutionRequest>(r => captured = r), Arg.Any<CancellationToken>())
            .Returns(new BenefitResolutionResult { Success = true });
        var ctx = CobContext("S",
            new CobOutcome { PendReason = "cob-payer-order-mismatch", Scenario = CobScenario.ChoPrimaryNoSecondary },
            ClaimAdjudicationStageResult.Pend("CoordinationOfBenefits", "mismatch"));

        await _sut.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Null(captured!.Cob);
        Assert.Equal(AdjudicationExecutionMode.Prospective, captured.ExecutionMode);
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
