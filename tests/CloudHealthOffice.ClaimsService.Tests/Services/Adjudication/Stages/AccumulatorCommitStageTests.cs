using ClaimsService.Models;
using ClaimsService.Services.Adjudication;
using ClaimsService.Services.Adjudication.Stages;
using CloudHealthOffice.BenefitEngine.Models;
using CloudHealthOffice.BenefitEngine.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace CloudHealthOffice.ClaimsService.Tests.Services.Adjudication.Stages;

/// <summary>
/// The pipeline commits a claim's accumulators only when every stage passed,
/// never for an examiner-approval re-run (the resolver commits after its
/// fenced final write), and never passes a claim without them.
/// </summary>
public class AccumulatorCommitStageTests
{
    private readonly IBenefitCalculationEngine _engine = Substitute.For<IBenefitCalculationEngine>();
    private readonly AccumulatorCommitStage _sut;

    public AccumulatorCommitStageTests()
    {
        _sut = new AccumulatorCommitStage(_engine, NullLogger<AccumulatorCommitStage>.Instance);
        _engine.CommitAccumulatorsAsync(Arg.Any<AccumulatorCommit>(), Arg.Any<CancellationToken>())
            .Returns(AccumulatorCommitOutcome.Committed);
    }

    private static readonly AccumulatorCommit Prepared = new()
    {
        CommitId = "c-1", ClaimId = "CLM-1", MemberId = "M1", SubscriberId = "M1",
        BenefitPlanId = Guid.NewGuid(), PlanYear = "2026",
    };

    private static ClaimAdjudicationContext Context(AccumulatorCommit? commit, params ClaimAdjudicationStageResult[] results)
    {
        var ctx = new ClaimAdjudicationContext
        {
            TenantId = "t1",
            ClaimVersionId = "CLM-1",
            Claim = new AdapterClaim { Id = "CLM-1", TenantId = "t1", MemberId = "M1" },
            BenefitResolutionResult = new BenefitResolutionResult { Success = true, PreparedAccumulatorCommit = commit },
        };
        ctx.StageResults.AddRange(results);
        return ctx;
    }

    [Fact]
    public void RunsAfterEveryReviewStage_BeforePersistence_AndIsRequired()
    {
        Assert.InRange(_sut.Order, 601, PersistenceStage_Order - 1);
        Assert.True(_sut.IsRequired);
    }

    private const int PersistenceStage_Order = 999;

    [Fact]
    public async Task EveryStagePassed_CommitsThePreparedWrite()
    {
        var result = await _sut.ExecuteAsync(Context(Prepared, ClaimAdjudicationStageResult.Pass("BenefitCalculation")), default);

        Assert.Equal(ClaimAdjudicationOutcome.Pass, result.Outcome);
        await _engine.Received(1).CommitAccumulatorsAsync(Prepared, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(ClaimAdjudicationOutcome.Pend)]
    [InlineData(ClaimAdjudicationOutcome.Deny)]
    [InlineData(ClaimAdjudicationOutcome.Reject)]
    public async Task AnyStagePendedDeniedOrRejected_CommitsNothing(ClaimAdjudicationOutcome outcome)
    {
        var later = outcome switch
        {
            ClaimAdjudicationOutcome.Pend => ClaimAdjudicationStageResult.Pend("NcciEdits", "bundled pair"),
            ClaimAdjudicationOutcome.Deny => ClaimAdjudicationStageResult.Deny("NcciEdits", "denied"),
            _ => ClaimAdjudicationStageResult.Reject("AiExamination", "threw"),
        };

        var result = await _sut.ExecuteAsync(
            Context(Prepared, ClaimAdjudicationStageResult.Pass("BenefitCalculation"), later), default);

        Assert.Equal(ClaimAdjudicationOutcome.Pass, result.Outcome);
        await _engine.DidNotReceiveWithAnyArgs().CommitAccumulatorsAsync(default!, default);
    }

    [Fact]
    public async Task ApprovalRerun_DefersTheCommitToTheResolver()
    {
        var ctx = Context(Prepared, ClaimAdjudicationStageResult.Pass("BenefitCalculation"));
        ctx.ExaminerApproval = new ExaminerApproval { ExaminerId = "e1", ResolutionLockToken = "lock" };

        await _sut.ExecuteAsync(ctx, default);

        await _engine.DidNotReceiveWithAnyArgs().CommitAccumulatorsAsync(default!, default);
    }

    /// <summary>A benefit-plan-service build that prepares no commit: never pass the claim without accumulators.</summary>
    [Fact]
    public async Task PassingClaimWithoutAPreparedCommit_FailsTheRun()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _sut.ExecuteAsync(Context(null, ClaimAdjudicationStageResult.Pass("BenefitCalculation")), default));
    }

    [Fact]
    public async Task ClaimFencedInTheStore_IsRejected()
    {
        _engine.CommitAccumulatorsAsync(Arg.Any<AccumulatorCommit>(), Arg.Any<CancellationToken>())
            .Returns(AccumulatorCommitOutcome.RefusedClaimReversed);

        var result = await _sut.ExecuteAsync(Context(Prepared, ClaimAdjudicationStageResult.Pass("BenefitCalculation")), default);

        Assert.Equal(ClaimAdjudicationOutcome.Reject, result.Outcome);
    }

    [Fact]
    public async Task ACommitFailure_Throws_SoTheMessageIsRedelivered()
    {
        _engine.CommitAccumulatorsAsync(Arg.Any<AccumulatorCommit>(), Arg.Any<CancellationToken>())
            .Returns<AccumulatorCommitOutcome>(_ => throw new HttpRequestException("503"));

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            _sut.ExecuteAsync(Context(Prepared, ClaimAdjudicationStageResult.Pass("BenefitCalculation")), default));
    }
}
