using ClaimsService.Models;
using ClaimsService.Models.Adjudication;
using ClaimsService.Services.Adjudication;
using ClaimsService.Services.Adjudication.Shadow;
using ClaimsService.Services.Adjudication.Stages;
using ClaimsService.Services.Resolution;
using CloudHealthOffice.BenefitEngine.Domain;
using CloudHealthOffice.BenefitEngine.Models;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace CloudHealthOffice.ClaimsService.Tests.Services.Adjudication.Stages;

/// <summary>Shadow adjudication prices from the core's accumulation, never CHO's store.</summary>
public partial class BenefitCalculationStageTests
{
    private readonly IShadowAccumulatorHistory _history = Substitute.For<IShadowAccumulatorHistory>();

    private BenefitCalculationStage ShadowStage() => new(
        _engine, _memberResolver, _authorizationValidationClient,
        NullLogger<BenefitCalculationStage>.Instance, shadowHistory: _history);

    private ClaimAdjudicationContext Context(bool shadow)
    {
        var claim = BuildClaim(Guid.NewGuid().ToString());
        return new ClaimAdjudicationContext
        {
            TenantId = "tenant-1",
            ClaimVersionId = claim.Id,
            Claim = claim,
            IsShadow = shadow,
            PricingResult = PricedAt(claim, 72m),
            ResolvedMember = new ResolvedMember { MemberId = "MEM-1", IsSubscriber = true },
        };
    }

    private static readonly PriorAccumulation CoreHistory = new()
    {
        Entries = [new PriorAccumulationEntry { ServiceDate = new DateOnly(2026, 2, 1), ThisMember = true, Deductible = 400m, OutOfPocket = 400m }],
    };

    private static BenefitResolutionResult Priced(AccumulatorCommit? commit) => new()
    {
        Success = true,
        PreparedAccumulatorCommit = commit,
        Totals = new ClaimTotals { TotalAllowed = 72m, TotalPlanPaid = 72m },
    };

    [Fact]
    public async Task Shadow_PricesFromTheCoresHistory_AndPassesWithoutACommit()
    {
        var ctx = Context(shadow: true);
        _history.BuildAsync(ctx, "MEM-1", Arg.Any<CancellationToken>()).Returns(CoreHistory);
        _engine.CalculateAsync(Arg.Any<BenefitResolutionRequest>(), Arg.Any<CancellationToken>()).Returns(Priced(null));

        var result = await ShadowStage().ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pass, result.Outcome);
        await _engine.Received(1).CalculateAsync(
            Arg.Is<BenefitResolutionRequest>(r => r.PriorAccumulation == CoreHistory),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Shadow_WithoutAHistorySource_StillKeepsOffTheStore()
    {
        var ctx = Context(shadow: true);
        _engine.CalculateAsync(Arg.Any<BenefitResolutionRequest>(), Arg.Any<CancellationToken>()).Returns(Priced(null));

        await _sut.ExecuteAsync(ctx, CancellationToken.None);

        await _engine.Received(1).CalculateAsync(
            Arg.Is<BenefitResolutionRequest>(r => r.PriorAccumulation != null && r.PriorAccumulation.Entries.Count == 0),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RealAdjudication_NeverSendsAHistory()
    {
        var ctx = Context(shadow: false);
        _engine.CalculateAsync(Arg.Any<BenefitResolutionRequest>(), Arg.Any<CancellationToken>()).Returns(Priced(Prepared));

        var result = await ShadowStage().ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pass, result.Outcome);
        await _history.DidNotReceiveWithAnyArgs().BuildAsync(default!, default!, default);
        await _engine.Received(1).CalculateAsync(
            Arg.Is<BenefitResolutionRequest>(r => r.PriorAccumulation == null), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Shadow_AnOlderEngineThatPreparesACommit_Pends()
    {
        var ctx = Context(shadow: true);
        _history.BuildAsync(ctx, "MEM-1", Arg.Any<CancellationToken>()).Returns(CoreHistory);
        _engine.CalculateAsync(Arg.Any<BenefitResolutionRequest>(), Arg.Any<CancellationToken>()).Returns(Priced(Prepared));

        var result = await ShadowStage().ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pend, result.Outcome);
        Assert.Equal(ClaimAdjudicationOrchestrator.ServiceUnavailablePendCode, ctx.PendDetails!.PendCode);
        Assert.Equal(BenefitCalculationStage.ShadowHistoryUnsupportedReason, ctx.PendDetails.PendReason);
    }

    [Fact]
    public async Task Shadow_HistoryUnreadable_PendsWithoutPricing()
    {
        var ctx = Context(shadow: true);
        _history.BuildAsync(ctx, "MEM-1", Arg.Any<CancellationToken>()).ThrowsAsync(new TimeoutException());

        var result = await ShadowStage().ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pend, result.Outcome);
        Assert.Equal(ClaimAdjudicationOrchestrator.ServiceUnavailablePendCode, ctx.PendDetails!.PendCode);
        await _engine.DidNotReceiveWithAnyArgs().CalculateAsync(default!, default);
    }
}
