using ClaimsService.Models;
using ClaimsService.Repositories;
using ClaimsService.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace CloudHealthOffice.ClaimsService.Tests.Services;

/// <summary>
/// Capability 5.12a — covers <see cref="ClaimFinalizationService.VoidAsync"/>
/// added per Gap 1 ratification. The method owns the
/// Paid/PartiallyPaid/Adjusted → Voided transition; emits
/// <c>ClaimVersionVoided</c> and <c>claims.finalized.v1</c> with the
/// existing Voided→"Reversed" Kafka mapping convention.
/// </summary>
public class ClaimFinalizationServiceVoidAsyncTests
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
    public async Task VoidAsync_PaidClaim_TransitionsToVoidedAndEmitsEvents()
    {
        var claim = PaidClaim();
        _repo.GetByIdAsync("c1").Returns(claim);
        _repo.MarkVoidedProjectionAsync("t1", "c1", Arg.Any<DateTime>(), "actor-1", Arg.Any<CancellationToken>())
            .Returns(true);

        var sut = CreateService();
        var result = await sut.VoidAsync(
            "c1",
            new ClaimVoidRequest { Reason = "Adjustment ReversalRun", ReversalRunId = "rr-1" },
            "t1", "actor-1", "corr-1");

        Assert.Equal(ClaimVoidOutcome.Voided, result.Outcome);
        await _repo.Received(1).MarkVoidedProjectionAsync(
            "t1", "c1", Arg.Any<DateTime>(), "actor-1", Arg.Any<CancellationToken>());
        await _versionPublisher.Received(1).PublishVersionVoidedAsync(
            Arg.Any<Claim>(), "Adjustment ReversalRun", "actor-1", "corr-1", Arg.Any<CancellationToken>());
        await _kafkaPublisher.Received(1).PublishClaimFinalizedAsync(Arg.Any<Claim>(), "t1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task VoidAsync_AlreadyVoided_IsIdempotentNoOp()
    {
        var claim = PaidClaim();
        claim.Status = ClaimStatus.Voided;
        claim.VersionState = ClaimVersionState.Voided;
        _repo.GetByIdAsync("c1").Returns(claim);

        var sut = CreateService();
        var result = await sut.VoidAsync(
            "c1", new ClaimVoidRequest { Reason = "double-void test" }, "t1", "actor-1", "corr-1");

        Assert.Equal(ClaimVoidOutcome.AlreadyVoided, result.Outcome);
        await _repo.DidNotReceiveWithAnyArgs().MarkVoidedProjectionAsync(default!, default!, default, default, default);
        await _versionPublisher.DidNotReceiveWithAnyArgs().PublishVersionVoidedAsync(default!, default, default, default, default);
        await _kafkaPublisher.DidNotReceiveWithAnyArgs().PublishClaimFinalizedAsync(default!, default!, default);
    }

    [Fact]
    public async Task VoidAsync_AdjustedSourceState_AcceptedViaVersionStatePath()
    {
        var claim = PaidClaim();
        claim.Status = ClaimStatus.Paid;       // Status untouched after supersession
        claim.VersionState = ClaimVersionState.Adjusted;
        _repo.GetByIdAsync("c1").Returns(claim);
        _repo.MarkVoidedProjectionAsync(default!, default!, default, default, default)
            .ReturnsForAnyArgs(true);

        var sut = CreateService();
        var result = await sut.VoidAsync(
            "c1", new ClaimVoidRequest { Reason = "ReversalRun completing supersession" },
            "t1", "actor-1", "corr-1");

        Assert.Equal(ClaimVoidOutcome.Voided, result.Outcome);
    }

    [Theory]
    [InlineData(ClaimStatus.Submitted)]
    [InlineData(ClaimStatus.Pended)]
    [InlineData(ClaimStatus.Approved)]
    [InlineData(ClaimStatus.Denied)]
    public async Task VoidAsync_NonVoidEligibleSourceState_Returns422(ClaimStatus status)
    {
        var claim = PaidClaim();
        claim.Status = status;
        claim.VersionState = ClaimVersionState.Submitted;  // ensure VersionState path doesn't accept it
        _repo.GetByIdAsync("c1").Returns(claim);

        var sut = CreateService();
        var result = await sut.VoidAsync(
            "c1", new ClaimVoidRequest { Reason = "test" }, "t1", "actor-1", "corr-1");

        Assert.Equal(ClaimVoidOutcome.InvalidSourceState, result.Outcome);
    }

    [Fact]
    public async Task VoidAsync_PartiallyPaid_AcceptedAsVoidEligible()
    {
        var claim = PaidClaim();
        claim.Status = ClaimStatus.PartiallyPaid;
        _repo.GetByIdAsync("c1").Returns(claim);
        _repo.MarkVoidedProjectionAsync(default!, default!, default, default, default).ReturnsForAnyArgs(true);

        var sut = CreateService();
        var result = await sut.VoidAsync(
            "c1", new ClaimVoidRequest { Reason = "test" }, "t1", "actor-1", "corr-1");

        Assert.Equal(ClaimVoidOutcome.Voided, result.Outcome);
    }

    [Fact]
    public async Task VoidAsync_UnknownClaimId_Returns404()
    {
        _repo.GetByIdAsync("missing").Returns((Claim?)null);

        var sut = CreateService();
        var result = await sut.VoidAsync(
            "missing", new ClaimVoidRequest { Reason = "test" }, "t1", "actor-1", "corr-1");

        Assert.Equal(ClaimVoidOutcome.NotFound, result.Outcome);
    }

    [Fact]
    public async Task VoidAsync_WrongTenant_Returns404()
    {
        var claim = PaidClaim(tenantId: "OTHER");
        _repo.GetByIdAsync("c1").Returns(claim);

        var sut = CreateService();
        var result = await sut.VoidAsync(
            "c1", new ClaimVoidRequest { Reason = "test" }, "t1", "actor-1", "corr-1");

        Assert.Equal(ClaimVoidOutcome.NotFound, result.Outcome);
    }

    [Fact]
    public async Task VoidAsync_ProjectionWriteFailure_Returns404()
    {
        var claim = PaidClaim();
        _repo.GetByIdAsync("c1").Returns(claim);
        _repo.MarkVoidedProjectionAsync(default!, default!, default, default, default).ReturnsForAnyArgs(false);

        var sut = CreateService();
        var result = await sut.VoidAsync(
            "c1", new ClaimVoidRequest { Reason = "test" }, "t1", "actor-1", "corr-1");

        Assert.Equal(ClaimVoidOutcome.NotFound, result.Outcome);
    }

    [Fact]
    public async Task VoidAsync_KafkaEmitProceedsAfterVersionPublisherFailure()
    {
        var claim = PaidClaim();
        _repo.GetByIdAsync("c1").Returns(claim);
        _repo.MarkVoidedProjectionAsync(default!, default!, default, default, default).ReturnsForAnyArgs(true);
        _versionPublisher
            .PublishVersionVoidedAsync(default!, default, default, default, default)
            .ReturnsForAnyArgs<ClaimVersionEvent>(_ => throw new InvalidOperationException("Mongo down"));

        var sut = CreateService();
        var result = await sut.VoidAsync(
            "c1", new ClaimVoidRequest { Reason = "test" }, "t1", "actor-1", "corr-1");

        Assert.Equal(ClaimVoidOutcome.Voided, result.Outcome);
        await _kafkaPublisher.Received(1).PublishClaimFinalizedAsync(Arg.Any<Claim>(), "t1", Arg.Any<CancellationToken>());
    }
}
