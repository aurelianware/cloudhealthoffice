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
/// The PAUNAVAIL / PLANUNAVAIL bulk re-run (#1299 S1), its re-verification
/// follow-ups (S-A: a deny is final only when no check failed; S-B: the
/// denial's reversal fence; the batch's fair share and rotation).
/// </summary>
public sealed class PriorAuthUnavailableRerunTests
{
    private const string Tenant = "tenant-1";
    private static readonly string PlanId = Guid.NewGuid().ToString();

    private readonly IClaimRepository _claims = Substitute.For<IClaimRepository>();
    private readonly IClaimApprovalReadjudicator _readjudicator = Substitute.For<IClaimApprovalReadjudicator>();
    private readonly AccumulatorOperationsService _sut;
    private readonly Dictionary<string, Claim> _stored = new();

    public PriorAuthUnavailableRerunTests()
    {
        _sut = new AccumulatorOperationsService(
            _claims, Substitute.For<IClaimVersionEventPublisher>(), Substitute.For<IClaimEventPublisher>(),
            NullLogger<AccumulatorOperationsService>.Instance, _readjudicator);
        _claims.TryAcquireResolutionLockAsync(default!, default!, default!, default!, default, default, default)
            .ReturnsForAnyArgs(true);
        _claims.GetForAccumulatorOutboxAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => _stored.TryGetValue(ci.ArgAt<string>(1), out var c) ? c : null);
        _claims.UpdateHoldingResolutionLockAsync(Arg.Any<Claim>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<Claim>());
        _claims.FindPendedByPendCodeAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Claim>());
    }

    private Claim Pended(string id, string code)
    {
        var claim = new Claim
        {
            Id = id,
            TenantId = Tenant,
            ClaimNumber = "CLM-" + id,
            MemberId = "MEM-1",
            BenefitPlanId = PlanId,
            Status = ClaimStatus.Pended,
            ServiceDateFrom = new DateTime(2026, 4, 15),
            PendDetails = new PendDetails { PendCode = code, PendReason = code + " reason", PendedAt = DateTime.UtcNow },
        };
        _stored[id] = claim;
        return claim;
    }

    private void Pending(string code, params Claim[] claims) =>
        _claims.FindPendedByPendCodeAsync(code, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ci => claims.Take(ci.ArgAt<int>(1)).ToList());

    private void RerunAnswers(ApprovalReadjudicationResult result) =>
        _readjudicator.ReadjudicateForApprovalAsync(default!, default!, default!, default)
            .ReturnsForAnyArgs(result);

    // ── S-A: a deny is final only when no check failed ────────────────

    /// <summary>
    /// PA still unavailable, and another stage (NCCI in Deny mode) denies: the
    /// claim must not be denied while the PA lookup is down.
    /// </summary>
    [Theory]
    [InlineData("NcciEdits: NCCI PTP edit: bundled pair (deny mode)")]
    [InlineData("NcciEdits: NCCI engine threw: InvalidOperationException")]
    public async Task Deny_WhileAPriorAuthLookupIsStillUnavailable_StaysPended(string denyingStage)
    {
        Pending(BenefitCalculationStage.PriorAuthorizationUnavailablePendCode, Pended("C1", BenefitCalculationStage.PriorAuthorizationUnavailablePendCode));
        RerunAnswers(new ApprovalReadjudicationResult(ClaimAdjudicationOutcome.Deny,
            $"{BenefitCalculationStage.StageName}: {BenefitCalculationStage.PriorAuthorizationUnavailableReason} (line 1: 503)",
            [$"{BenefitCalculationStage.StageName}: {BenefitCalculationStage.PriorAuthorizationUnavailableReason} (line 1: 503)", denyingStage])
        {
            DenialReason = denyingStage,
        });

        var run = await _sut.RerunPriorAuthUnavailablePendsAsync(Tenant, "ops-1", 10, CancellationToken.None);

        Assert.Equal(0, run.Denied);
        Assert.Equal(1, run.StillPended);
        await _claims.DidNotReceiveWithAnyArgs().UpdateHoldingResolutionLockAsync(default!, default!, default);
    }

    /// <summary>The audit records the denying stage's own reason, not the first non-passing stage's.</summary>
    [Fact]
    public async Task Deny_RecordsTheDenyingStagesReason()
    {
        Pending(BenefitCalculationStage.PriorAuthorizationUnavailablePendCode, Pended("C1", BenefitCalculationStage.PriorAuthorizationUnavailablePendCode));
        var denial = $"{BenefitCalculationStage.StageName}: {BenefitCalculationStage.PriorAuthorizationRequiredReason}";
        RerunAnswers(new ApprovalReadjudicationResult(ClaimAdjudicationOutcome.Deny,
            "CoordinationOfBenefits: confirm the payer order",
            ["CoordinationOfBenefits: confirm the payer order", denial])
        {
            DenialReason = denial,
        });

        var run = await _sut.RerunPriorAuthUnavailablePendsAsync(Tenant, "ops-1", 10, CancellationToken.None);

        Assert.Equal(1, run.Denied);
        await _claims.Received(1).UpdateHoldingResolutionLockAsync(
            Arg.Is<Claim>(c => c.Status == ClaimStatus.Denied
                               && c.ExaminerResolutions.Single().Reason!.Contains(BenefitCalculationStage.PriorAuthorizationRequiredReason)
                               && !c.ExaminerResolutions.Single().Reason!.Contains("payer order")),
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ── S-B: the denial carries the reversal fence ────────────────────

    [Fact]
    public async Task Deny_WritesTheTerminalReversalWithTheDenial()
    {
        Pending(BenefitCalculationStage.PriorAuthorizationUnavailablePendCode, Pended("C1", BenefitCalculationStage.PriorAuthorizationUnavailablePendCode));
        var denial = $"{BenefitCalculationStage.StageName}: {BenefitCalculationStage.PriorAuthorizationRequiredReason}";
        RerunAnswers(new ApprovalReadjudicationResult(ClaimAdjudicationOutcome.Deny, denial, [denial]) { DenialReason = denial });

        await _sut.RerunPriorAuthUnavailablePendsAsync(Tenant, "ops-1", 10, CancellationToken.None);

        await _claims.Received(1).UpdateHoldingResolutionLockAsync(
            Arg.Is<Claim>(c => c.Status == ClaimStatus.Denied
                               && c.PendingAccumulatorReversal != null
                               && c.PendingAccumulatorReversal.Reversal!.ClaimId == "C1"
                               && c.PendingAccumulatorCommit == null),
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ── Batch: fair share per pend code ───────────────────────────────

    /// <summary>A backlog of PAUNAVAIL pends must not starve PLANUNAVAIL out of the batch.</summary>
    [Fact]
    public async Task Batch_GivesEachPendCodeAFairShare()
    {
        Pending(BenefitCalculationStage.PriorAuthorizationUnavailablePendCode,
            Enumerable.Range(1, 5).Select(i => Pended("PA" + i, BenefitCalculationStage.PriorAuthorizationUnavailablePendCode)).ToArray());
        Pending(BenefitCalculationStage.PlanUnavailablePendCode,
            Enumerable.Range(1, 5).Select(i => Pended("PL" + i, BenefitCalculationStage.PlanUnavailablePendCode)).ToArray());
        RerunAnswers(new ApprovalReadjudicationResult(ClaimAdjudicationOutcome.Pend, "still unavailable"));

        var run = await _sut.RerunPriorAuthUnavailablePendsAsync(Tenant, "ops-1", 4, CancellationToken.None);

        Assert.Equal(4, run.Examined);
        Assert.Equal(2, run.Claims.Count(c => c.ClaimId.StartsWith("PA")));
        Assert.Equal(2, run.Claims.Count(c => c.ClaimId.StartsWith("PL")));
    }

    /// <summary>When one code has fewer pends than its share, the other fills the batch.</summary>
    [Fact]
    public async Task Batch_UnusedShareGoesToTheOtherCode()
    {
        Pending(BenefitCalculationStage.PriorAuthorizationUnavailablePendCode,
            Enumerable.Range(1, 5).Select(i => Pended("PA" + i, BenefitCalculationStage.PriorAuthorizationUnavailablePendCode)).ToArray());
        Pending(BenefitCalculationStage.PlanUnavailablePendCode, Pended("PL1", BenefitCalculationStage.PlanUnavailablePendCode));
        RerunAnswers(new ApprovalReadjudicationResult(ClaimAdjudicationOutcome.Pend, "still unavailable"));

        var run = await _sut.RerunPriorAuthUnavailablePendsAsync(Tenant, "ops-1", 4, CancellationToken.None);

        Assert.Equal(4, run.Examined);
        Assert.Equal(1, run.Claims.Count(c => c.ClaimId.StartsWith("PL")));
    }

    /// <summary>The lock is taken for the request tenant (tenant-scoped).</summary>
    [Fact]
    public async Task Rerun_TakesTheLockInTheRequestTenant()
    {
        Pending(BenefitCalculationStage.PriorAuthorizationUnavailablePendCode, Pended("C1", BenefitCalculationStage.PriorAuthorizationUnavailablePendCode));
        RerunAnswers(new ApprovalReadjudicationResult(ClaimAdjudicationOutcome.Pend, "still unavailable"));

        await _sut.RerunPriorAuthUnavailablePendsAsync(Tenant, "ops-1", 10, CancellationToken.None);

        await _claims.Received(1).TryAcquireResolutionLockAsync(
            Tenant, "C1", Arg.Any<string>(), Arg.Is<string>(a => a.StartsWith(AccumulatorOperationsService.PriorAuthRerunActorPrefix)),
            Arg.Any<DateTime>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
        await _readjudicator.Received(1).ReadjudicateForApprovalAsync(
            Tenant, "C1", Arg.Any<ExaminerApproval>(), Arg.Any<CancellationToken>());
    }
}
