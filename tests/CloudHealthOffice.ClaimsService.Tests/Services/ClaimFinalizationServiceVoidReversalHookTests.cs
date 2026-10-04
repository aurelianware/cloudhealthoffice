using ClaimsService.Models;
using ClaimsService.Repositories;
using ClaimsService.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace CloudHealthOffice.ClaimsService.Tests.Services;

/// <summary>
/// Capability 5.12b — covers the
/// <see cref="IClaimAdjustmentService.MarkActiveOnReversalAsync"/>
/// hook fired by <see cref="ClaimFinalizationService.VoidAsync"/> when
/// the request carries a non-null <c>ReversalRunId</c>. The hook is
/// what closes the loop between operator-initiated ReversalRun and the
/// 5.12a adjustment lifecycle.
/// </summary>
public class ClaimFinalizationServiceVoidReversalHookTests
{
    private readonly IClaimRepository _repo = Substitute.For<IClaimRepository>();
    private readonly IClaimVersionEventPublisher _versionPublisher = Substitute.For<IClaimVersionEventPublisher>();
    private readonly IClaimEventPublisher _kafkaPublisher = Substitute.For<IClaimEventPublisher>();
    private readonly IClaimAdjustmentService _adjustmentService = Substitute.For<IClaimAdjustmentService>();

    private ClaimFinalizationService CreateService() =>
        new(_repo, _versionPublisher, _kafkaPublisher, _adjustmentService, NullLogger<ClaimFinalizationService>.Instance);

    private static Claim PaidClaim(string id = "c1", string tenantId = "t1") => new()
    {
        Id = id,
        TenantId = tenantId,
        ClaimVersionId = id,
        VersionNumber = 1,
        VersionState = ClaimVersionState.Paid,
        ClaimNumber = "CLM-001",
        Status = ClaimStatus.Paid,
        AdjudicationResult = new AdjudicationResult { CheckNumber = "CHK-001", PayerPayment = 800m },
        ServiceDateFrom = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
        ServiceDateTo = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
        BillingProviderNPI = "1234567890",
        LineOfBusiness = LineOfBusiness.Commercial,
        MemberId = "m1",
        TotalChargeAmount = 1000m,
    };

    [Fact]
    public async Task VoidAsync_WithReversalRunId_FiresAdjustmentLifecycleHook()
    {
        var claim = PaidClaim();
        _repo.GetByIdAsync("c1").Returns(claim, claim);
        _repo.MarkVoidedProjectionAsync("t1", "c1", Arg.Any<DateTime>(), "actor", default)
            .Returns(true);

        var result = await CreateService().VoidAsync(
            "c1",
            new ClaimVoidRequest { Reason = "operator reverse", ReversalRunId = "rr-1" },
            "t1", "actor", "corr");

        Assert.Equal(ClaimVoidOutcome.Voided, result.Outcome);
        await _adjustmentService.Received(1)
            .MarkActiveOnReversalAsync("t1", "c1", "rr-1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task VoidAsync_WithoutReversalRunId_DoesNotFireHook()
    {
        // Operator-initiated void (no ReversalRun correlation): the
        // hook is bypassed entirely.
        var claim = PaidClaim();
        _repo.GetByIdAsync("c1").Returns(claim, claim);
        _repo.MarkVoidedProjectionAsync("t1", "c1", Arg.Any<DateTime>(), "actor", default)
            .Returns(true);

        var result = await CreateService().VoidAsync(
            "c1",
            new ClaimVoidRequest { Reason = "ops manual void" },
            "t1", "actor", "corr");

        Assert.Equal(ClaimVoidOutcome.Voided, result.Outcome);
        await _adjustmentService.DidNotReceiveWithAnyArgs()
            .MarkActiveOnReversalAsync(default!, default!, default!, default);
    }

    [Fact]
    public async Task VoidAsync_AlreadyVoided_WithReversalRunId_DrivesTheAdjustmentTransition_WithoutReVoiding()
    {
        // Before: an already-voided claim returned early, so if the first
        // void persisted but its PendingReversal -> Active step failed, no
        // retry could ever complete it. A repeat void now re-drives that
        // (idempotent) step; it does not void again or re-emit events.
        var claim = PaidClaim();
        claim.Status = ClaimStatus.Voided;
        claim.VersionState = ClaimVersionState.Voided;
        _repo.GetByIdAsync("c1").Returns(claim);

        var result = await CreateService().VoidAsync(
            "c1",
            new ClaimVoidRequest { Reason = "retry", ReversalRunId = "rr-1" },
            "t1", "actor", "corr");

        Assert.Equal(ClaimVoidOutcome.AlreadyVoided, result.Outcome);
        await _adjustmentService.Received(1)
            .MarkActiveOnReversalAsync("t1", "c1", "rr-1", Arg.Any<CancellationToken>());
        await _repo.DidNotReceiveWithAnyArgs().MarkVoidedProjectionAsync(default!, default!, default, default, default);
        await _versionPublisher.DidNotReceiveWithAnyArgs()
            .PublishVersionVoidedAsync(default!, default!, default, default, default);
        await _kafkaPublisher.DidNotReceiveWithAnyArgs().PublishClaimFinalizedAsync(default!, default!, default);
    }

    [Fact]
    public async Task VoidAsync_AlreadyVoided_WithoutReversalRunId_DoesNotFireHook()
    {
        var claim = PaidClaim();
        claim.Status = ClaimStatus.Voided;
        claim.VersionState = ClaimVersionState.Voided;
        _repo.GetByIdAsync("c1").Returns(claim);

        var result = await CreateService().VoidAsync(
            "c1", new ClaimVoidRequest { Reason = "retry" }, "t1", "actor", "corr");

        Assert.Equal(ClaimVoidOutcome.AlreadyVoided, result.Outcome);
        await _adjustmentService.DidNotReceiveWithAnyArgs()
            .MarkActiveOnReversalAsync(default!, default!, default!, default);
    }

    [Fact]
    public async Task VoidAsync_AlreadyVoided_HookThrows_StillAlreadyVoided()
    {
        var claim = PaidClaim();
        claim.Status = ClaimStatus.Voided;
        claim.VersionState = ClaimVersionState.Voided;
        _repo.GetByIdAsync("c1").Returns(claim);
        _adjustmentService
            .MarkActiveOnReversalAsync("t1", "c1", "rr-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("downstream blip")));

        var result = await CreateService().VoidAsync(
            "c1", new ClaimVoidRequest { Reason = "retry", ReversalRunId = "rr-1" }, "t1", "actor", "corr");

        Assert.Equal(ClaimVoidOutcome.AlreadyVoided, result.Outcome);
    }

    [Fact]
    public async Task RepeatVoid_AfterAdjustmentStepFailed_CompletesTheAdjustment_Once()
    {
        // End to end with the real ClaimAdjustmentService: the first void
        // persists but the adjustment write fails, leaving it
        // PendingReversal. The retry (same reversal run) moves it to Active;
        // a further retry changes nothing. One void, one set of events.
        var claim = PaidClaim();
        var voided = PaidClaim();
        voided.Status = ClaimStatus.Voided;
        voided.VersionState = ClaimVersionState.Voided;
        _repo.GetByIdAsync("c1").Returns(claim, voided);
        _repo.MarkVoidedProjectionAsync("t1", "c1", Arg.Any<DateTime>(), "actor", Arg.Any<CancellationToken>())
            .Returns(true);

        var adjustment = new ClaimAdjustment
        {
            Id = "adj-1",
            TenantId = "t1",
            ClaimVersionId = "c1",
            PredecessorClaimId = "c1",
            PredecessorVersionId = "c1",
            NewClaimId = "c2",
            AdjustmentReason = "correction",
            IdempotencyKey = "idem-1",
            RequestHash = "hash",
            CreatedBy = "actor",
            Status = ClaimAdjustmentStatus.PendingReversal,
        };
        var adjustmentRepo = Substitute.For<IClaimAdjustmentRepository>();
        adjustmentRepo
            .GetByPredecessorAndStatusAsync("t1", "c1", ClaimAdjustmentStatus.PendingReversal, Arg.Any<CancellationToken>())
            .Returns(_ => adjustment.Status == ClaimAdjustmentStatus.PendingReversal
                ? Clone(adjustment)
                : null);
        var updates = 0;
        adjustmentRepo.UpdateAsync(Arg.Any<ClaimAdjustment>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                if (++updates == 1)
                    throw new TimeoutException("adjustment store unavailable");
                adjustment = ci.Arg<ClaimAdjustment>();
                return adjustment;
            });
        var adjustmentService = new ClaimAdjustmentService(
            Substitute.For<IClaimRepository>(),
            adjustmentRepo,
            Substitute.For<IClaimSubmissionService>(),
            Substitute.For<IClaimVersionEventPublisher>(),
            Substitute.For<CloudHealthOffice.Infrastructure.Messaging.IMessageBus>(),
            NullLogger<ClaimAdjustmentService>.Instance);
        var service = new ClaimFinalizationService(
            _repo, _versionPublisher, _kafkaPublisher, adjustmentService, NullLogger<ClaimFinalizationService>.Instance);
        var request = new ClaimVoidRequest { Reason = "reversal run", ReversalRunId = "rr-1" };

        var first = await service.VoidAsync("c1", request, "t1", "actor", "corr");
        Assert.Equal(ClaimVoidOutcome.Voided, first.Outcome);
        Assert.Equal(ClaimAdjustmentStatus.PendingReversal, adjustment.Status);

        var second = await service.VoidAsync("c1", request, "t1", "actor", "corr");
        Assert.Equal(ClaimVoidOutcome.AlreadyVoided, second.Outcome);
        Assert.Equal(ClaimAdjustmentStatus.Active, adjustment.Status);
        Assert.Equal("rr-1", adjustment.ReversalRunId);
        Assert.NotNull(adjustment.ReversalCompletedAt);

        var third = await service.VoidAsync("c1", request, "t1", "actor", "corr");
        Assert.Equal(ClaimVoidOutcome.AlreadyVoided, third.Outcome);
        Assert.Equal(2, updates);

        await _repo.Received(1).MarkVoidedProjectionAsync("t1", "c1", Arg.Any<DateTime>(), "actor", Arg.Any<CancellationToken>());
        await _versionPublisher.Received(1)
            .PublishVersionVoidedAsync(Arg.Any<Claim>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await _kafkaPublisher.Received(1).PublishClaimFinalizedAsync(Arg.Any<Claim>(), "t1", Arg.Any<CancellationToken>());
    }

    private static ClaimAdjustment Clone(ClaimAdjustment a)
        => System.Text.Json.JsonSerializer.Deserialize<ClaimAdjustment>(System.Text.Json.JsonSerializer.Serialize(a))!;

    [Fact]
    public async Task VoidAsync_HookThrows_VoidStillSucceeds()
    {
        // Hook failure is non-blocking: the void has persisted and
        // emitted; lifecycle transition can be re-driven by a follow-up
        // sweep / operator intervention.
        var claim = PaidClaim();
        _repo.GetByIdAsync("c1").Returns(claim, claim);
        _repo.MarkVoidedProjectionAsync("t1", "c1", Arg.Any<DateTime>(), "actor", default)
            .Returns(true);
        _adjustmentService
            .MarkActiveOnReversalAsync("t1", "c1", "rr-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("downstream blip")));

        var result = await CreateService().VoidAsync(
            "c1",
            new ClaimVoidRequest { Reason = "operator reverse", ReversalRunId = "rr-1" },
            "t1", "actor", "corr");

        Assert.Equal(ClaimVoidOutcome.Voided, result.Outcome);
    }
}
