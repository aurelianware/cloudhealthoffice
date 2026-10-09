using System.Net;
using System.Net.Http.Json;
using ClaimsService.Models;
using ClaimsService.Models.Adjudication;
using ClaimsService.Repositories;
using ClaimsService.Services;
using ClaimsService.Services.Adjudication;
using CloudHealthOffice.BenefitEngine.Services;
using CloudHealthOffice.Infrastructure.Security;
using NSubstitute;
using Xunit;

namespace CloudHealthOffice.ClaimsService.Tests.Controllers;

/// <summary>
/// PR #1278 round 3 — the examiner resolve flow:
/// B1 the re-run overrides only the reviewed (persisted) pends;
/// B2 payerSequence is range-checked, permissioned, needs a second approver
///    as primary over a prior payment, and is audited on the claim;
/// H4 a denial reverses the engine accumulators;
/// L9 publishing after the decision is not cancellable;
/// L10 a held resolution lock refuses a concurrent resolution;
/// plus the workqueue:work permission on /resolve.
/// </summary>
public class ExaminerResolutionTests : IClassFixture<ClaimsApiFactory>
{
    private readonly ClaimsApiFactory _factory;
    private readonly IClaimRepository _repo;
    private readonly IClaimApprovalReadjudicator _readjudicator;
    private readonly IClaimVersionEventPublisher _versionPublisher;
    private readonly IBenefitCalculationEngine _engine;

    public ExaminerResolutionTests(ClaimsApiFactory factory)
    {
        _factory = factory;
        _repo = factory.ClaimRepository;
        _readjudicator = factory.ApprovalReadjudicator;
        _versionPublisher = factory.VersionEventPublisher;
        _engine = factory.BenefitEngine;
        _repo.ClearReceivedCalls();
        _readjudicator.ClearReceivedCalls();
        _versionPublisher.ClearReceivedCalls();
        _engine.ClearReceivedCalls();
        _readjudicator.ReadjudicateForApprovalAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<ExaminerApproval>(), Arg.Any<CancellationToken>())
            .Returns(new ApprovalReadjudicationResult(ClaimAdjudicationOutcome.Pass, null)
            {
                OverriddenPends = ["DuplicateClaim: DUPLICATE: possible duplicate"],
            });
        _repo.TryAcquireResolutionLockAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(),
                Arg.Any<DateTime>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _repo.UpdateAsync(Arg.Any<Claim>()).Returns(call => call.Arg<Claim>());
        _repo.UpdateHoldingResolutionLockAsync(Arg.Any<Claim>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Claim>());
    }

    private HttpClient Client(string user, string role)
    {
        var client = _factory.CreateDefaultClient(new ChoDevelopmentTokenHandler(user, role));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", "test-tenant");
        return client;
    }

    private static Claim Pended(string id, string code, string reason, string sbr01 = "P", params ClaimOtherPayer[] others)
    {
        var claim = new Claim
        {
            Id = id,
            TenantId = "test-tenant",
            ClaimNumber = id,
            Status = ClaimStatus.Pended,
            VersionState = ClaimVersionState.Submitted,
            BillingProviderNPI = "1234567890",
            LineOfBusiness = LineOfBusiness.Commercial,
            MemberId = "MEM-9",
            BenefitPlanId = "8b9f1a2e-1111-4c4c-9a9a-000000000001",
            PayerResponsibilityCode = sbr01,
            TotalChargeAmount = 580m,
            ServiceDateFrom = new DateTime(2026, 4, 19, 0, 0, 0, DateTimeKind.Utc),
            ServiceDateTo = new DateTime(2026, 4, 19, 0, 0, 0, DateTimeKind.Utc),
            PendDetails = new PendDetails { PendCode = code, PendReason = reason, PendedAt = DateTime.UtcNow },
            ClaimLines = [new() { LineNumber = 1, ProcedureCode = "99214", ChargeAmount = 580m, Units = 1 }],
        };
        claim.OtherPayers.AddRange(others);
        return claim;
    }

    private static ClaimOtherPayer Payer(string sbr01, decimal paid) =>
        new() { PayerResponsibilityCode = sbr01, PayerName = $"Payer {sbr01}", PaidAmount = paid };

    /// <summary>Golden 07's shape: tertiary per SBR01; the primary and secondary paid $172.</summary>
    private static Claim Golden07CobPend() =>
        Pended("claim-g07", "COB", "cob-payer-order-mismatch", "T", Payer("P", 112m), Payer("S", 60m));

    /// <summary>
    /// Posts a resolution; an approval carries the fingerprint of the claim's
    /// pends as the examiner viewed them unless the body sets its own.
    /// </summary>
    private Task<HttpResponseMessage> Resolve(HttpClient client, Claim claim, object body, bool withFingerprint = true)
    {
        _repo.GetByIdAsync(claim.Id).Returns(claim);
        var json = System.Text.Json.JsonSerializer.SerializeToNode(body)!.AsObject();
        if (withFingerprint && !json.ContainsKey("pendFingerprint"))
            json["pendFingerprint"] = claim.PendDetails?.Fingerprint;
        return client.PostAsJsonAsync($"/api/claims/work-queue/{claim.Id}/resolve", json);
    }

    // ── B1 ────────────────────────────────────────────────────────────

    /// <summary>
    /// The re-run is told exactly what the examiner reviewed: the persisted
    /// pend (code and reason, plus every additional reason). The audit
    /// record keeps what the re-run overrode.
    /// </summary>
    [Fact]
    public async Task Approval_PassesThePersistedPend_AndRecordsTheAudit()
    {
        var claim = Pended("claim-dup", "DUPLICATE", "possible duplicate");
        claim.PendDetails!.AdditionalPendReasons.Add("NCCI: bundled pair NE001");

        var response = await Resolve(Client("examiner-1", ChoRolePermissions.ClaimsExaminer), claim,
            new { disposition = "Approved", reason = "not a duplicate" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await _readjudicator.Received(1).ReadjudicateForApprovalAsync(
            "test-tenant", claim.Id,
            Arg.Is<ExaminerApproval>(a =>
                a.ReviewedPend!.PendCode == "DUPLICATE"
                && a.ReviewedPends.Count == 2
                && a.ReviewedPends[1].Code == "NCCI"
                && a.ExaminerId == "examiner-1"),
            Arg.Any<CancellationToken>());
        await _repo.Received().UpdateHoldingResolutionLockAsync(Arg.Is<Claim>(saved =>
            saved.Status == ClaimStatus.Approved
            && saved.ExaminerResolutions.Count == 1
            && saved.ExaminerResolutions[0].ApproverIds.SequenceEqual(new[] { "examiner-1" })
            && saved.ExaminerResolutions[0].Reason == "not a duplicate"
            && saved.ExaminerResolutions[0].ReviewedPends.SequenceEqual(new[] { "DUPLICATE: possible duplicate", "NCCI: bundled pair NE001" })
            && saved.ExaminerResolutions[0].OverriddenPends.SequenceEqual(new[] { "DuplicateClaim: DUPLICATE: possible duplicate" })
            && saved.ResolutionLock == null), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    /// <summary>A pend the examiner did not review (a new one on the re-run) refuses the approval.</summary>
    [Fact]
    public async Task Approval_NewPendOnTheRerun_Is409_NothingSaved()
    {
        _readjudicator.ReadjudicateForApprovalAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<ExaminerApproval>(), Arg.Any<CancellationToken>())
            .Returns(new ApprovalReadjudicationResult(ClaimAdjudicationOutcome.Pend, "x",
                ["ProviderIntegrity: Billing: Provider integrity check could not be reached."]));
        var claim = Pended("claim-dup2", "DUPLICATE", "possible duplicate");

        var response = await Resolve(Client("examiner-1", ChoRolePermissions.ClaimsExaminer), claim,
            new { disposition = "Approved", reason = "ok" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("Provider integrity check could not be reached", await response.Content.ReadAsStringAsync());
        await _repo.DidNotReceive().UpdateHoldingResolutionLockAsync(Arg.Any<Claim>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _repo.Received(1).ReleaseResolutionLockAsync("test-tenant", claim.Id, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ── B2 ────────────────────────────────────────────────────────────

    /// <summary>
    /// Round-3 verification: COB is among the stored pends but not the
    /// routing one (a duplicate pended first). payerSequence is accepted and
    /// the re-run is told about both pends.
    /// </summary>
    [Fact]
    public async Task PayerSequence_WhenCobIsAnAdditionalPend_IsAccepted()
    {
        var claim = Pended("claim-dup-cob", "DUPLICATE", "possible duplicate", "S", Payer("P", 50m));
        claim.PendDetails!.AdditionalPendReasons.Add("COB: cob-secondary-not-supported-phase-1");

        var response = await Resolve(Client("examiner-1", ChoRolePermissions.ClaimsExaminer), claim,
            new { disposition = "Approved", reason = "secondary per EOB", payerSequence = 2 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await _readjudicator.Received(1).ReadjudicateForApprovalAsync(
            "test-tenant", claim.Id,
            Arg.Is<ExaminerApproval>(a => a.PayerSequence == 2 && a.ReviewedPends.Count == 2),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PayerSequence_OnANonCobPend_Is400()
    {
        var claim = Pended("claim-ncci", "NCCI", "bundled pair", "S", Payer("P", 50m));

        var response = await Resolve(Client("supervisor-1", ChoRolePermissions.ClaimsSupervisor), claim,
            new { disposition = "Approved", reason = "x", payerSequence = 2 });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await _readjudicator.DidNotReceiveWithAnyArgs().ReadjudicateForApprovalAsync(default!, default!, default!, default);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(-1)]
    public async Task PayerSequence_OutOfRange_Is400_NotClamped(int sequence)
    {
        var response = await Resolve(Client("supervisor-1", ChoRolePermissions.ClaimsSupervisor), Golden07CobPend(),
            new { disposition = "Approved", reason = "x", payerSequence = sequence });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("between 1 and 3", await response.Content.ReadAsStringAsync());
        await _readjudicator.DidNotReceiveWithAnyArgs().ReadjudicateForApprovalAsync(default!, default!, default!, default);
    }

    /// <summary>Disagreeing with SBR01 needs claims:override-approve — an examiner gets 403.</summary>
    [Fact]
    public async Task PayerSequence_DisagreeingWithSbr01_WithoutOverridePermission_Is403()
    {
        var response = await Resolve(Client("examiner-1", ChoRolePermissions.ClaimsExaminer), Golden07CobPend(),
            new { disposition = "Approved", reason = "x", payerSequence = 2 });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task PayerSequence_DisagreeingWithSbr01_WithoutReason_Is400()
    {
        var response = await Resolve(Client("supervisor-1", ChoRolePermissions.ClaimsSupervisor), Golden07CobPend(),
            new { disposition = "Approved", payerSequence = 2 });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// The reviewer's case: golden 07 approved as primary (payerSequence 1)
    /// paid $152 on top of $172 already paid against $250 allowed. One
    /// supervisor can no longer do that: the first approval waits for a
    /// second, different approver (202, nothing re-run); the same approver
    /// again is refused; a second supervisor completes it, and both are
    /// recorded.
    /// </summary>
    [Fact]
    public async Task PrimaryOverPriorPayment_NeedsASecondDifferentApprover()
    {
        var claim = Golden07CobPend();

        var first = await Resolve(Client("supervisor-1", ChoRolePermissions.ClaimsSupervisor), claim,
            new { disposition = "Approved", reason = "other coverage terminated", payerSequence = 1 });

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        await _readjudicator.DidNotReceiveWithAnyArgs().ReadjudicateForApprovalAsync(default!, default!, default!, default);
        await _repo.Received(1).UpdateHoldingResolutionLockAsync(Arg.Is<Claim>(c =>
            c.Status == ClaimStatus.Pended
            && c.PendingExaminerApproval!.RequestedBy == "supervisor-1"
            && c.PendingExaminerApproval.PayerSequence == 1), Arg.Any<string>(), Arg.Any<CancellationToken>());

        claim.PendingExaminerApproval = new PendingExaminerApproval
        {
            RequestedBy = "supervisor-1", PayerSequence = 1, Reason = "other coverage terminated", RequestedAt = DateTime.UtcNow,
            PendFingerprint = claim.PendDetails!.Fingerprint, ExpiresAt = DateTime.UtcNow.AddHours(72),
        };
        var same = await Resolve(Client("supervisor-1", ChoRolePermissions.ClaimsSupervisor), claim,
            new { disposition = "Approved", reason = "again", payerSequence = 1 });
        Assert.Equal(HttpStatusCode.Conflict, same.StatusCode);
        await _readjudicator.DidNotReceiveWithAnyArgs().ReadjudicateForApprovalAsync(default!, default!, default!, default);

        var second = await Resolve(Client("supervisor-2", ChoRolePermissions.ClaimsSupervisor), claim,
            new { disposition = "Approved", reason = "confirmed with the member", payerSequence = 1 });

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        await _readjudicator.Received(1).ReadjudicateForApprovalAsync(
            "test-tenant", claim.Id,
            Arg.Is<ExaminerApproval>(a =>
                a.ExaminerId == "supervisor-1" && a.SecondApproverId == "supervisor-2"
                && a.PayerSequence == 1 && a.PayerOrderOverrideAuthorized),
            Arg.Any<CancellationToken>());
        await _repo.Received().UpdateHoldingResolutionLockAsync(Arg.Is<Claim>(c =>
            c.Status == ClaimStatus.Approved
            && c.PendingExaminerApproval == null
            && c.ExaminerResolutions.Single().ApproverIds.SequenceEqual(new[] { "supervisor-1", "supervisor-2" })
            && c.ExaminerResolutions.Single().ApproverReasons.SequenceEqual(new[] { "other coverage terminated", "confirmed with the member" })
            && c.ExaminerResolutions.Single().PayerSequence == 1), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    /// <summary>/resolve requires workqueue:work (it had no permission at all).</summary>
    [Fact]
    public async Task Resolve_WithoutWorkQueuePermission_Is403()
    {
        var response = await Resolve(Client("ms-1", ChoRolePermissions.MemberServices),
            Pended("claim-ms", "DUPLICATE", "possible duplicate"),
            new { disposition = "Approved", reason = "x" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await _readjudicator.DidNotReceiveWithAnyArgs().ReadjudicateForApprovalAsync(default!, default!, default!, default);
    }

    // ── Round-3 verification ──────────────────────────────────────────

    /// <summary>
    /// M4: the approval names the pends the examiner viewed (fingerprint).
    /// The claim was re-adjudicated since and its pends changed: 409 with the
    /// current pends; nothing re-run.
    /// </summary>
    [Fact]
    public async Task Approval_WithAStaleFingerprint_Is409_NoRerun()
    {
        var claim = Pended("claim-fp", "DUPLICATE", "possible duplicate");
        var viewed = claim.PendDetails!.Fingerprint;
        claim.PendDetails.AdditionalPendReasons.Add("MEDREVIEW: Billing: manual review required.");

        var response = await Resolve(Client("examiner-1", ChoRolePermissions.ClaimsExaminer), claim,
            new { disposition = "Approved", reason = "ok", pendFingerprint = viewed });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("pends changed", body);
        Assert.Contains(claim.PendDetails.Fingerprint, body);
        await _readjudicator.DidNotReceiveWithAnyArgs().ReadjudicateForApprovalAsync(default!, default!, default!, default);
    }

    [Fact]
    public async Task Approval_WithoutAFingerprint_Is400()
    {
        var response = await Resolve(Client("examiner-1", ChoRolePermissions.ClaimsExaminer),
            Pended("claim-nofp", "DUPLICATE", "possible duplicate"),
            new { disposition = "Approved", reason = "ok" }, withFingerprint: false);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await _readjudicator.DidNotReceiveWithAnyArgs().ReadjudicateForApprovalAsync(default!, default!, default!, default);
    }

    /// <summary>
    /// M5: a waiting first approval does not count once it has expired, or
    /// once the claim's pends changed — the second call starts over (202).
    /// </summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task PendingFirstApproval_ExpiredOrForOtherPends_DoesNotCount(bool expired, bool otherPends)
    {
        var claim = Golden07CobPend();
        claim.PendingExaminerApproval = new PendingExaminerApproval
        {
            RequestedBy = "supervisor-1", PayerSequence = 1, Reason = "r1",
            RequestedAt = DateTime.UtcNow.AddHours(-80),
            PendFingerprint = otherPends ? "20260101T000000000Z-000000000000000000000000" : claim.PendDetails!.Fingerprint,
            ExpiresAt = expired ? DateTime.UtcNow.AddHours(-8) : DateTime.UtcNow.AddHours(64),
        };

        var response = await Resolve(Client("supervisor-2", ChoRolePermissions.ClaimsSupervisor), claim,
            new { disposition = "Approved", reason = "r2", payerSequence = 1 });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        await _readjudicator.DidNotReceiveWithAnyArgs().ReadjudicateForApprovalAsync(default!, default!, default!, default);
        await _repo.Received(1).UpdateHoldingResolutionLockAsync(Arg.Is<Claim>(c =>
                c.PendingExaminerApproval!.RequestedBy == "supervisor-2"
                && c.PendingExaminerApproval.PendFingerprint == claim.PendDetails!.Fingerprint
                && c.PendingExaminerApproval.ExpiresAt > DateTime.UtcNow.AddHours(71)),
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// L6: the final write is fenced on the lock token. A resolver whose lock
    /// was taken over (its re-run outlived the lock) cannot finalize: 409 and
    /// nothing is published.
    /// </summary>
    [Fact]
    public async Task LostLockAtTheFinalWrite_Is409_NothingPublished()
    {
        _repo.UpdateHoldingResolutionLockAsync(Arg.Any<Claim>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((Claim?)null);

        var response = await Resolve(Client("examiner-1", ChoRolePermissions.ClaimsExaminer),
            Pended("claim-lost", "DUPLICATE", "possible duplicate"), new { disposition = "Approved", reason = "x" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await _versionPublisher.DidNotReceiveWithAnyArgs().PublishVersionResolvedAsync(default!, default!, default, default, default, default);
        _repo.UpdateHoldingResolutionLockAsync(Arg.Any<Claim>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Claim>());
    }

    /// <summary>
    /// L7: an invalid aiExaminerAgreement is rejected before anything
    /// happens — no re-run for an approval, no accumulator reversal for a
    /// denial (it used to be checked after both).
    /// </summary>
    [Theory]
    [InlineData("Approved")]
    [InlineData("Denied")]
    public async Task InvalidAiAgreement_Is400_BeforeAnySideEffect(string disposition)
    {
        var claim = Pended("claim-ai", "NCCI", "bundled pair");
        claim.AiExamination = new AiExamination { RecommendedDisposition = "Approve" };

        var response = await Resolve(Client("examiner-1", ChoRolePermissions.ClaimsExaminer), claim,
            new { disposition, reason = "x", aiExaminerAgreement = "Maybe" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await _readjudicator.DidNotReceiveWithAnyArgs().ReadjudicateForApprovalAsync(default!, default!, default!, default);
        await _engine.DidNotReceiveWithAnyArgs().ReverseClaimAsync(default!, default!, default, default, default!, default);
        await _repo.DidNotReceiveWithAnyArgs().TryAcquireResolutionLockAsync(default!, default!, default!, default, default, default, default);
    }

    // ── H4 ────────────────────────────────────────────────────────────

    /// <summary>
    /// A claim pended by NCCI (Order 400) priced in Production at 300 and
    /// wrote engine accumulators; the examiner's denial reverses them.
    /// </summary>
    [Fact]
    public async Task Deny_ReversesTheEngineAccumulators()
    {
        var claim = Pended("claim-deny", "NCCI", "bundled pair");

        var response = await Resolve(Client("examiner-1", ChoRolePermissions.ClaimsExaminer), claim,
            new { disposition = "Denied", reason = "bundled" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await _engine.Received(1).ReverseClaimAsync(
            "MEM-9", "MEM-9", Guid.Parse(claim.BenefitPlanId!), DateOnly.FromDateTime(claim.ServiceDateFrom),
            claim.Id, Arg.Any<CancellationToken>());
        await _readjudicator.DidNotReceiveWithAnyArgs().ReadjudicateForApprovalAsync(default!, default!, default!, default);
    }

    // ── L9 / L10 ──────────────────────────────────────────────────────

    [Fact]
    public async Task AfterTheDecision_PublishingIsNotCancellable()
    {
        await Resolve(Client("examiner-1", ChoRolePermissions.ClaimsExaminer),
            Pended("claim-l9", "DUPLICATE", "possible duplicate"), new { disposition = "Approved", reason = "x" });

        await _versionPublisher.Received(1).PublishVersionResolvedAsync(
            Arg.Any<Claim>(), "Approved", "x", "examiner-1", Arg.Any<string>(),
            Arg.Is<CancellationToken>(t => !t.CanBeCanceled));
    }

    [Fact]
    public async Task ConcurrentResolution_LockHeld_Is409_NoRerun()
    {
        _repo.TryAcquireResolutionLockAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(),
                Arg.Any<DateTime>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(false);

        var response = await Resolve(Client("examiner-1", ChoRolePermissions.ClaimsExaminer),
            Pended("claim-l10", "DUPLICATE", "possible duplicate"), new { disposition = "Approved", reason = "x" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await _readjudicator.DidNotReceiveWithAnyArgs().ReadjudicateForApprovalAsync(default!, default!, default!, default);
        await _versionPublisher.DidNotReceiveWithAnyArgs().PublishVersionResolvedAsync(default!, default!, default, default, default, default);
    }
}
