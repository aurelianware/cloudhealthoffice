using ClaimsService.Models;
using ClaimsService.Repositories;
using ClaimsService.Services;
using ClaimsService.Services.Adjudication;
using CloudHealthOffice.Infrastructure.Messaging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace CloudHealthOffice.ClaimsService.Tests.Services;

/// <summary>
/// Capability 5.12b — covers <see cref="ClaimAdjustmentService"/>'s
/// orchestrator-finalize callback (<see cref="IClaimAdjustmentService.OnNewVersionFinalizedAsync"/>)
/// and reversal-completion callback
/// (<see cref="IClaimAdjustmentService.MarkActiveOnReversalAsync"/>).
/// These two methods wire the lifecycle transitions
/// AwaitingReadjudication → PendingReversal/Failed and
/// PendingReversal → Active that 5.12a deliberately deferred to 5.12b.
/// </summary>
public class ClaimAdjustmentServiceLifecycleTests
{
    private readonly IClaimRepository _claimRepository = Substitute.For<IClaimRepository>();
    private readonly IClaimAdjustmentRepository _adjustmentRepository = Substitute.For<IClaimAdjustmentRepository>();
    private readonly IClaimSubmissionService _submissionService = Substitute.For<IClaimSubmissionService>();
    private readonly IClaimVersionEventPublisher _versionPublisher = Substitute.For<IClaimVersionEventPublisher>();
    private readonly IMessageBus _messageBus = Substitute.For<IMessageBus>();

    private ClaimAdjustmentService CreateService() => new(
        _claimRepository,
        _adjustmentRepository,
        _submissionService,
        _versionPublisher,
        _messageBus,
        NullLogger<ClaimAdjustmentService>.Instance);

    private static ClaimAdjustment AwaitingReadjudication() => new()
    {
        Id = "adj-1",
        TenantId = "t1",
        ClaimVersionId = "chain-1",
        PredecessorClaimId = "pred-1",
        PredecessorVersionId = "pred-1",
        NewClaimId = "new-1",
        AdjustmentReason = "operator correction",
        IdempotencyKey = "idem-1",
        RequestHash = "hash",
        CreatedBy = "actor",
        Status = ClaimAdjustmentStatus.AwaitingReadjudication,
    };

    [Fact]
    public async Task OnNewVersionFinalizedAsync_PassOutcome_TransitionsToPendingReversal()
    {
        var adjustment = AwaitingReadjudication();
        _adjustmentRepository.GetByNewClaimIdAsync("t1", "new-1").Returns(adjustment);

        await CreateService().OnNewVersionFinalizedAsync("t1", "new-1", ClaimAdjudicationOutcome.Pass);

        Assert.Equal(ClaimAdjustmentStatus.PendingReversal, adjustment.Status);
        Assert.NotNull(adjustment.ReadjudicationCompletedAt);
        Assert.Null(adjustment.FailureReason);
        await _adjustmentRepository.Received(1).UpdateAsync(adjustment);
    }

    [Fact]
    public async Task OnNewVersionFinalizedAsync_DenyOutcome_TransitionsToPendingReversal()
    {
        // Deny on the corrected version still means the predecessor's
        // accumulator impact + provider payment need unwinding via
        // ReversalRun. The corrected version being denied doesn't roll
        // back the original payment by itself.
        var adjustment = AwaitingReadjudication();
        _adjustmentRepository.GetByNewClaimIdAsync("t1", "new-1").Returns(adjustment);

        await CreateService().OnNewVersionFinalizedAsync("t1", "new-1", ClaimAdjudicationOutcome.Deny);

        Assert.Equal(ClaimAdjustmentStatus.PendingReversal, adjustment.Status);
        await _adjustmentRepository.Received(1).UpdateAsync(adjustment);
    }

    [Fact]
    public async Task OnNewVersionFinalizedAsync_RejectOutcome_TransitionsToFailedWithReason()
    {
        var adjustment = AwaitingReadjudication();
        _adjustmentRepository.GetByNewClaimIdAsync("t1", "new-1").Returns(adjustment);

        await CreateService().OnNewVersionFinalizedAsync("t1", "new-1", ClaimAdjudicationOutcome.Reject);

        Assert.Equal(ClaimAdjustmentStatus.Failed, adjustment.Status);
        Assert.NotNull(adjustment.FailureReason);
        Assert.Contains("Reject", adjustment.FailureReason);
        await _adjustmentRepository.Received(1).UpdateAsync(adjustment);
    }

    [Fact]
    public async Task OnNewVersionFinalizedAsync_PendOutcome_NoTransition()
    {
        // Pend = corrected version still in human-review limbo.
        // Adjustment stays AwaitingReadjudication until pipeline reaches
        // a real terminal state.
        var adjustment = AwaitingReadjudication();
        _adjustmentRepository.GetByNewClaimIdAsync("t1", "new-1").Returns(adjustment);

        await CreateService().OnNewVersionFinalizedAsync("t1", "new-1", ClaimAdjudicationOutcome.Pend);

        Assert.Equal(ClaimAdjustmentStatus.AwaitingReadjudication, adjustment.Status);
        await _adjustmentRepository.DidNotReceiveWithAnyArgs().UpdateAsync(default!);
    }

    [Fact]
    public async Task OnNewVersionFinalizedAsync_NoMatchingAdjustment_NoOp()
    {
        // Most fresh submissions hit this path — no in-flight adjustment.
        _adjustmentRepository.GetByNewClaimIdAsync("t1", "fresh-claim").Returns((ClaimAdjustment?)null);

        await CreateService().OnNewVersionFinalizedAsync("t1", "fresh-claim", ClaimAdjudicationOutcome.Pass);

        await _adjustmentRepository.DidNotReceiveWithAnyArgs().UpdateAsync(default!);
    }

    [Fact]
    public async Task OnNewVersionFinalizedAsync_AlreadyTransitioned_Idempotent()
    {
        // Re-invocation against a non-AwaitingReadjudication adjustment
        // is a no-op so orchestrator at-least-once redelivery doesn't
        // corrupt downstream state.
        var adjustment = AwaitingReadjudication();
        adjustment.Status = ClaimAdjustmentStatus.PendingReversal;
        _adjustmentRepository.GetByNewClaimIdAsync("t1", "new-1").Returns(adjustment);

        await CreateService().OnNewVersionFinalizedAsync("t1", "new-1", ClaimAdjudicationOutcome.Pass);

        Assert.Equal(ClaimAdjustmentStatus.PendingReversal, adjustment.Status);
        await _adjustmentRepository.DidNotReceiveWithAnyArgs().UpdateAsync(default!);
    }

    [Fact]
    public async Task OnNewVersionFinalizedAsync_EmptyTenantOrClaimId_NoOp()
    {
        await CreateService().OnNewVersionFinalizedAsync("", "x", ClaimAdjudicationOutcome.Pass);
        await CreateService().OnNewVersionFinalizedAsync("t1", "", ClaimAdjudicationOutcome.Pass);
        await _adjustmentRepository.DidNotReceiveWithAnyArgs().GetByNewClaimIdAsync(default!, default!);
    }

    [Fact]
    public async Task MarkActiveOnReversalAsync_PendingReversalMatch_TransitionsToActive()
    {
        var adjustment = AwaitingReadjudication();
        adjustment.Status = ClaimAdjustmentStatus.PendingReversal;
        _adjustmentRepository
            .GetByPredecessorAndStatusAsync("t1", "pred-1", ClaimAdjustmentStatus.PendingReversal)
            .Returns(adjustment);

        await CreateService().MarkActiveOnReversalAsync("t1", "pred-1", "rr-1");

        Assert.Equal(ClaimAdjustmentStatus.Active, adjustment.Status);
        Assert.Equal("rr-1", adjustment.ReversalRunId);
        Assert.NotNull(adjustment.ReversalCompletedAt);
        await _adjustmentRepository.Received(1).UpdateAsync(adjustment);
    }

    [Fact]
    public async Task MarkActiveOnReversalAsync_NoPendingReversalAdjustment_NoOp()
    {
        // Operator-initiated void without ReversalRun, or void of a claim
        // that wasn't part of an adjustment chain.
        _adjustmentRepository
            .GetByPredecessorAndStatusAsync("t1", "pred-1", ClaimAdjustmentStatus.PendingReversal)
            .Returns((ClaimAdjustment?)null);

        await CreateService().MarkActiveOnReversalAsync("t1", "pred-1", "rr-1");

        await _adjustmentRepository.DidNotReceiveWithAnyArgs().UpdateAsync(default!);
    }

    [Fact]
    public async Task MarkActiveOnReversalAsync_EmptyReversalRunId_NoOp()
    {
        await CreateService().MarkActiveOnReversalAsync("t1", "pred-1", "");
        await _adjustmentRepository.DidNotReceiveWithAnyArgs().GetByPredecessorAndStatusAsync(
            default!, default!, default);
    }
}
