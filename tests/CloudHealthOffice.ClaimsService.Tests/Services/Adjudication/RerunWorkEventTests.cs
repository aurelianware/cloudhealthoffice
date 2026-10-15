using ClaimsService.Models;
using ClaimsService.Models.Adjudication;
using ClaimsService.Repositories;
using ClaimsService.Services;
using ClaimsService.Services.Adjudication;
using ClaimsService.Services.Adjudication.Stages;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace CloudHealthOffice.ClaimsService.Tests.Services.Adjudication;

/// <summary>
/// The bulk PAUNAVAIL re-run resolves pended claims without an examiner: a
/// claim it takes out of Pended completes its work item (claims.work.v1
/// work-resolved); a claim it leaves Pended is still work.
/// </summary>
public sealed class RerunWorkEventTests
{
    private const string Tenant = "tenant-1";
    private readonly IClaimRepository _claims = Substitute.For<IClaimRepository>();
    private readonly IClaimApprovalReadjudicator _readjudicator = Substitute.For<IClaimApprovalReadjudicator>();
    private readonly IClaimWorkEventPublisher _work = Substitute.For<IClaimWorkEventPublisher>();
    private readonly AccumulatorOperationsService _sut;
    private readonly Claim _claim;

    public RerunWorkEventTests()
    {
        var lookup = Substitute.For<IAccumulatorCommitLookup>();
        lookup.LookupCommitRecordsAsync(Arg.Any<IReadOnlyList<AccumulatorCommitLookupItem>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, bool>());
        _sut = new AccumulatorOperationsService(
            _claims, Substitute.For<IClaimVersionEventPublisher>(), Substitute.For<IClaimEventPublisher>(),
            NullLogger<AccumulatorOperationsService>.Instance, _readjudicator, lookup: lookup, workEvents: _work);
        _claim = new Claim
        {
            Id = "C1", TenantId = Tenant, ClaimNumber = "CLM-C1", MemberId = "MEM-1",
            BenefitPlanId = Guid.NewGuid().ToString(), Status = ClaimStatus.Pended,
            ServiceDateFrom = new DateTime(2026, 4, 15),
            PendDetails = new PendDetails { PendCode = BenefitCalculationStage.PriorAuthorizationUnavailablePendCode, PendReason = "down" },
        };
        _claims.TryAcquireResolutionLockAsync(default!, default!, default!, default!, default, default, default).ReturnsForAnyArgs(true);
        _claims.GetForAccumulatorOutboxAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_claim);
        _claims.UpdateHoldingResolutionLockAsync(Arg.Any<Claim>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<Claim>());
        _claims.FindPendedByPendCodeAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Claim>());
        _claims.FindPendedByPendCodeAsync(BenefitCalculationStage.PriorAuthorizationUnavailablePendCode, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<Claim> { _claim });
    }

    [Fact]
    public async Task ADefinitivePriorAuthDenial_CompletesTheWork()
    {
        _readjudicator.ReadjudicateForApprovalAsync(default!, default!, default!, default).ReturnsForAnyArgs(
            new ApprovalReadjudicationResult(ClaimAdjudicationOutcome.Deny, "PA required", ["PA required"])
            {
                DenialReason = "PA required",
                DenialReasonCode = BenefitCalculationStage.PriorAuthorizationRequiredCode,
            });

        var run = await _sut.RerunPriorAuthUnavailablePendsAsync(Tenant, "ops-1", 10, CancellationToken.None);

        Assert.Equal(1, run.Denied);
        await _work.Received(1).PublishWorkResolvedAsync(
            Arg.Is<Claim>(c => c.Id == "C1" && c.Status == ClaimStatus.Deny), Tenant, "Deny", Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AClaimLeftPended_IsStillWork()
    {
        _readjudicator.ReadjudicateForApprovalAsync(default!, default!, default!, default).ReturnsForAnyArgs(
            new ApprovalReadjudicationResult(ClaimAdjudicationOutcome.Pend, "still unavailable"));

        await _sut.RerunPriorAuthUnavailablePendsAsync(Tenant, "ops-1", 10, CancellationToken.None);

        await _work.DidNotReceiveWithAnyArgs().PublishWorkResolvedAsync(default!, default!, default!, default);
    }
}
