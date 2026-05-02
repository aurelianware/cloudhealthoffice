using ClaimsService.Models;
using ClaimsService.Repositories;
using ClaimsService.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace CloudHealthOffice.ClaimsService.Tests.Services;

public class ClaimFinalizationServiceTests
{
    private readonly IClaimRepository _repo = Substitute.For<IClaimRepository>();
    private readonly IClaimVersionEventPublisher _versionPublisher = Substitute.For<IClaimVersionEventPublisher>();
    private readonly IClaimEventPublisher _kafkaPublisher = Substitute.For<IClaimEventPublisher>();
    private readonly IClaimAdjustmentService _adjustmentService = Substitute.For<IClaimAdjustmentService>();

    private ClaimFinalizationService CreateService() =>
        new(_repo, _versionPublisher, _kafkaPublisher, _adjustmentService, NullLogger<ClaimFinalizationService>.Instance);

    private static Claim ApprovedClaim(string id = "c1", string tenantId = "t1") => new()
    {
        Id = id,
        TenantId = tenantId,
        ClaimVersionId = id,
        VersionNumber = 1,
        VersionState = ClaimVersionState.Adjudicated,
        ClaimNumber = "CLM-001",
        Status = ClaimStatus.Approved,
        AdjudicationResult = new AdjudicationResult { PayerPayment = 800m, AllowedAmount = 1000m },
        ServiceDateFrom = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
        ServiceDateTo = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
        BillingProviderNPI = "1234567890",
        LineOfBusiness = LineOfBusiness.Commercial,
        MemberId = "m1",
        TotalChargeAmount = 1000m
    };

    private static ClaimFinalizationRequest Request(string check = "CHK-001") => new()
    {
        CheckNumber = check,
        PaymentDate = new DateTime(2026, 5, 4, 0, 0, 0, DateTimeKind.Utc),
        PayerPayment = 800m,
        PaymentRunId = "run-1",
        EraEnvelopeId = "env-1"
    };

    [Fact]
    public async Task FinalizeAsync_ApprovedClaim_TransitionsToPaid()
    {
        var claim = ApprovedClaim();
        _repo.GetByIdAsync(claim.Id).Returns(claim);
        _repo.UpdateAsync(Arg.Any<Claim>()).Returns(call => call.Arg<Claim>());

        var result = await CreateService().FinalizeAsync(claim.Id, Request(), "t1", "actor", "corr");

        Assert.Equal(ClaimFinalizationOutcome.Finalized, result.Outcome);
        Assert.Equal(ClaimStatus.Paid, result.Claim!.Status);
        Assert.Equal(ClaimVersionState.Paid, result.Claim.VersionState);
        Assert.Equal("CHK-001", result.Claim.AdjudicationResult!.CheckNumber);
        Assert.Equal(800m, result.Claim.AdjudicationResult.PayerPayment);
        Assert.NotNull(result.Claim.PaidDate);

        await _versionPublisher.Received(1).PublishVersionPaidAsync(
            Arg.Is<Claim>(c => c.Status == ClaimStatus.Paid), "actor", "corr", Arg.Any<CancellationToken>());
        await _kafkaPublisher.Received(1).PublishClaimFinalizedAsync(
            Arg.Is<Claim>(c => c.Status == ClaimStatus.Paid), "t1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FinalizeAsync_PartiallyPaid_AlsoTransitionsToPaid()
    {
        var claim = ApprovedClaim();
        claim.Status = ClaimStatus.PartiallyPaid;
        _repo.GetByIdAsync(claim.Id).Returns(claim);
        _repo.UpdateAsync(Arg.Any<Claim>()).Returns(call => call.Arg<Claim>());

        var result = await CreateService().FinalizeAsync(claim.Id, Request(), "t1", null, null);

        Assert.Equal(ClaimFinalizationOutcome.Finalized, result.Outcome);
        Assert.Equal(ClaimStatus.Paid, result.Claim!.Status);
    }

    [Fact]
    public async Task FinalizeAsync_AlreadyPaidSameCheck_IsIdempotentNoOp()
    {
        var claim = ApprovedClaim();
        claim.Status = ClaimStatus.Paid;
        claim.VersionState = ClaimVersionState.Paid;
        claim.AdjudicationResult!.CheckNumber = "CHK-001";
        _repo.GetByIdAsync(claim.Id).Returns(claim);

        var result = await CreateService().FinalizeAsync(claim.Id, Request("CHK-001"), "t1", null, null);

        Assert.Equal(ClaimFinalizationOutcome.AlreadyFinalized, result.Outcome);
        await _repo.DidNotReceive().UpdateAsync(Arg.Any<Claim>());
        await _versionPublisher.DidNotReceive().PublishVersionPaidAsync(
            Arg.Any<Claim>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await _kafkaPublisher.DidNotReceive().PublishClaimFinalizedAsync(
            Arg.Any<Claim>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FinalizeAsync_AlreadyPaidDifferentCheck_ReturnsConflict()
    {
        var claim = ApprovedClaim();
        claim.Status = ClaimStatus.Paid;
        claim.AdjudicationResult!.CheckNumber = "CHK-EXISTING";
        _repo.GetByIdAsync(claim.Id).Returns(claim);

        var result = await CreateService().FinalizeAsync(claim.Id, Request("CHK-NEW"), "t1", null, null);

        Assert.Equal(ClaimFinalizationOutcome.Conflict, result.Outcome);
        Assert.Contains("CHK-EXISTING", result.Message);
        await _repo.DidNotReceive().UpdateAsync(Arg.Any<Claim>());
    }

    [Fact]
    public async Task FinalizeAsync_NonAdjudicatedClaim_ReturnsInvalidSourceState()
    {
        var claim = ApprovedClaim();
        claim.Status = ClaimStatus.Submitted;
        _repo.GetByIdAsync(claim.Id).Returns(claim);

        var result = await CreateService().FinalizeAsync(claim.Id, Request(), "t1", null, null);

        Assert.Equal(ClaimFinalizationOutcome.InvalidSourceState, result.Outcome);
        Assert.Contains("Submitted", result.Message);
        await _repo.DidNotReceive().UpdateAsync(Arg.Any<Claim>());
    }

    [Fact]
    public async Task FinalizeAsync_UnknownClaimId_ReturnsNotFound()
    {
        _repo.GetByIdAsync("missing").Returns((Claim?)null);

        var result = await CreateService().FinalizeAsync("missing", Request(), "t1", null, null);

        Assert.Equal(ClaimFinalizationOutcome.NotFound, result.Outcome);
    }

    [Fact]
    public async Task FinalizeAsync_VersionPublisherThrows_StillEmitsKafkaAndReturnsFinalized()
    {
        var claim = ApprovedClaim();
        _repo.GetByIdAsync(claim.Id).Returns(claim);
        _repo.UpdateAsync(Arg.Any<Claim>()).Returns(call => call.Arg<Claim>());
        _versionPublisher.PublishVersionPaidAsync(Arg.Any<Claim>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns<Task<ClaimVersionEvent>>(_ => throw new InvalidOperationException("event store down"));

        var result = await CreateService().FinalizeAsync(claim.Id, Request(), "t1", null, null);

        Assert.Equal(ClaimFinalizationOutcome.Finalized, result.Outcome);
        await _kafkaPublisher.Received(1).PublishClaimFinalizedAsync(
            Arg.Any<Claim>(), "t1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FinalizeAsync_PayerPaymentOmitted_PreservesExistingValue()
    {
        var claim = ApprovedClaim();
        claim.AdjudicationResult!.PayerPayment = 750m;
        _repo.GetByIdAsync(claim.Id).Returns(claim);
        _repo.UpdateAsync(Arg.Any<Claim>()).Returns(call => call.Arg<Claim>());

        var request = Request();
        request.PayerPayment = null;

        var result = await CreateService().FinalizeAsync(claim.Id, request, "t1", null, null);

        Assert.Equal(750m, result.Claim!.AdjudicationResult!.PayerPayment);
    }

    [Fact]
    public async Task FinalizeAsync_MissingCheckNumber_Throws()
    {
        var claim = ApprovedClaim();
        _repo.GetByIdAsync(claim.Id).Returns(claim);

        var request = Request();
        request.CheckNumber = string.Empty;

        await Assert.ThrowsAsync<ArgumentException>(() =>
            CreateService().FinalizeAsync(claim.Id, request, "t1", null, null));
    }

    [Fact]
    public async Task FinalizeAsync_WithEdiControlNumber_PersistsControlNumberInSameWrite()
    {
        var claim = ApprovedClaim();
        _repo.GetByIdAsync(claim.Id).Returns(claim);
        _repo.UpdateAsync(Arg.Any<Claim>()).Returns(call => call.Arg<Claim>());

        var request = Request();
        request.EdiControlNumber = "PR-20260501-A1B2";

        var result = await CreateService().FinalizeAsync(claim.Id, request, "t1", null, null);

        Assert.Equal(ClaimFinalizationOutcome.Finalized, result.Outcome);
        Assert.Equal("PR-20260501-A1B2", result.Claim!.EDI835ControlNumber);
        await _repo.Received(1).UpdateAsync(
            Arg.Is<Claim>(c => c.EDI835ControlNumber == "PR-20260501-A1B2" && c.Status == ClaimStatus.Paid));
    }

    [Fact]
    public async Task FinalizeAsync_WithoutEdiControlNumber_LeavesExistingValueUntouched()
    {
        var claim = ApprovedClaim();
        claim.EDI835ControlNumber = "EXISTING-CTRL";
        _repo.GetByIdAsync(claim.Id).Returns(claim);
        _repo.UpdateAsync(Arg.Any<Claim>()).Returns(call => call.Arg<Claim>());

        var request = Request();
        request.EdiControlNumber = null;

        var result = await CreateService().FinalizeAsync(claim.Id, request, "t1", null, null);

        Assert.Equal("EXISTING-CTRL", result.Claim!.EDI835ControlNumber);
    }
}
