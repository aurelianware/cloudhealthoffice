using ClaimsService.Models;
using ClaimsService.Repositories;
using ClaimsService.Services;
using CloudHealthOffice.BenefitEngine.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace CloudHealthOffice.ClaimsService.Tests.Services;

/// <summary>
/// PR #1278 M2 — a void (operator void, or the reversal run voiding the
/// version a replacement superseded) un-applies the claim in the benefit
/// engine's accumulator store via <see cref="IBenefitCalculationEngine.ReverseClaimAsync"/>.
/// </summary>
public class ClaimFinalizationServiceEngineReversalTests
{
    private readonly IClaimRepository _repo = Substitute.For<IClaimRepository>();
    private readonly IBenefitCalculationEngine _engine = Substitute.For<IBenefitCalculationEngine>();
    private static readonly Guid PlanId = Guid.NewGuid();

    private ClaimFinalizationService Service() => new(
        _repo,
        Substitute.For<IClaimVersionEventPublisher>(),
        Substitute.For<IClaimEventPublisher>(),
        Substitute.For<IClaimAdjustmentService>(),
        NullLogger<ClaimFinalizationService>.Instance,
        _engine);

    private static Claim PaidClaim(ClaimStatus status = ClaimStatus.Paid) => new()
    {
        Id = "c1",
        TenantId = "t1",
        ClaimVersionId = "c1",
        ClaimNumber = "CLM-001",
        Status = status,
        VersionState = status == ClaimStatus.Voided ? ClaimVersionState.Voided : ClaimVersionState.Paid,
        MemberId = "m1",
        SubscriberId = "s1",
        BenefitPlanId = PlanId.ToString(),
        ServiceDateFrom = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
        BillingProviderNPI = "1234567890",
    };

    [Fact]
    public async Task Void_ReversesTheEngineAccumulators()
    {
        var claim = PaidClaim();
        _repo.GetByIdAsync("c1").Returns(claim);
        _repo.MarkVoidedProjectionAsync("t1", "c1", Arg.Any<DateTime>(), "actor", Arg.Any<CancellationToken>()).Returns(true);

        var result = await Service().VoidAsync("c1", new ClaimVoidRequest { Reason = "void" }, "t1", "actor", null);

        Assert.Equal(ClaimVoidOutcome.Voided, result.Outcome);
        await _engine.Received(1).ReverseClaimAsync("m1", "s1", PlanId, new DateOnly(2026, 5, 1), "c1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RepeatVoid_ReDrivesTheEngineReversal_WhichIsIdempotent()
    {
        _repo.GetByIdAsync("c1").Returns(PaidClaim(ClaimStatus.Voided));

        var result = await Service().VoidAsync("c1", new ClaimVoidRequest { Reason = "void" }, "t1", "actor", null);

        Assert.Equal(ClaimVoidOutcome.AlreadyVoided, result.Outcome);
        await _engine.Received(1).ReverseClaimAsync("m1", "s1", PlanId, Arg.Any<DateOnly>(), "c1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EngineReversalFailure_DoesNotFailTheVoid()
    {
        var claim = PaidClaim();
        _repo.GetByIdAsync("c1").Returns(claim);
        _repo.MarkVoidedProjectionAsync("t1", "c1", Arg.Any<DateTime>(), "actor", Arg.Any<CancellationToken>()).Returns(true);
        _engine.ReverseClaimAsync(default!, default!, default, default, default!, default)
            .ReturnsForAnyArgs(Task.FromException(new HttpRequestException("down")));

        var result = await Service().VoidAsync("c1", new ClaimVoidRequest { Reason = "void" }, "t1", "actor", null);

        Assert.Equal(ClaimVoidOutcome.Voided, result.Outcome);
    }
}
