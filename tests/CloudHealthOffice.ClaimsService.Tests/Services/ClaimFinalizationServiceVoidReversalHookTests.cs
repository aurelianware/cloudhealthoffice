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
    public async Task VoidAsync_AlreadyVoided_DoesNotFireHook()
    {
        // Idempotent re-invocation of a previously-voided claim must not
        // re-fire the lifecycle transition (no double-update).
        var claim = PaidClaim();
        claim.Status = ClaimStatus.Voided;
        claim.VersionState = ClaimVersionState.Voided;
        _repo.GetByIdAsync("c1").Returns(claim);

        var result = await CreateService().VoidAsync(
            "c1",
            new ClaimVoidRequest { Reason = "retry", ReversalRunId = "rr-1" },
            "t1", "actor", "corr");

        Assert.Equal(ClaimVoidOutcome.AlreadyVoided, result.Outcome);
        await _adjustmentService.DidNotReceiveWithAnyArgs()
            .MarkActiveOnReversalAsync(default!, default!, default!, default);
    }

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
