using System.Diagnostics;
using ClaimsService.Controllers;
using ClaimsService.Models;
using ClaimsService.Repositories;
using ClaimsService.Services.Adjudication;
using ClaimsService.Models.Adjudication;
using CloudHealthOffice.Infrastructure.Security;
using CloudHealthOffice.Infrastructure.Logging;

namespace ClaimsService.Services;

/// <summary>
/// Examiner resolution of a pended (or Pay / Deny) claim — the application
/// logic behind <c>POST /api/claims/work-queue/{claimId}/resolve</c> and
/// <c>/override</c>. The controller binds and authorizes the request, calls
/// <see cref="ResolveAsync"/>, and maps the <see cref="ClaimResolutionResult"/>
/// to its HTTP response.
/// </summary>
public interface IClaimResolutionService
{
    /// <summary>
    /// Resolves the claim. <paramref name="getTenantId"/> is read only when
    /// the resolution needs the tenant (after the claim was found and its
    /// state checked), so a missing tenant fails at the same point it always
    /// did. <paramref name="traceIdentifier"/> is the request's trace id,
    /// used for correlation when there is no current activity.
    /// </summary>
    Task<ClaimResolutionResult> ResolveAsync(
        string claimId,
        ResolvePendedClaimRequest request,
        Func<string> getTenantId,
        string traceIdentifier,
        CancellationToken requestAborted);
}

/// <summary>
/// The outcome of <see cref="IClaimResolutionService.ResolveAsync"/>; each
/// case is one HTTP response of the resolve endpoint. Bodies are carried as
/// built, so the response is exactly what the endpoint always returned.
/// </summary>
public abstract record ClaimResolutionResult
{
    private ClaimResolutionResult() { }

    /// <summary>200 with the resolved claim.</summary>
    public sealed record Resolved(Claim Claim) : ClaimResolutionResult;

    /// <summary>202: a first approval recorded, waiting for a second approver.</summary>
    public sealed record AwaitingSecondApproval(object Body) : ClaimResolutionResult;

    /// <summary>400 with a message.</summary>
    public sealed record Invalid(string Message) : ClaimResolutionResult;

    /// <summary>404, no body.</summary>
    public sealed record ClaimNotFound : ClaimResolutionResult;

    /// <summary>409 with a body (a message or an object).</summary>
    public sealed record Conflict(object Body) : ClaimResolutionResult;

    /// <summary>422 with a body.</summary>
    public sealed record Unprocessable(object Body) : ClaimResolutionResult;

    /// <summary>A ProblemDetails response.</summary>
    public sealed record Problem(int StatusCode, string Title, string Detail) : ClaimResolutionResult;
}

/// <summary>
/// Default <see cref="IClaimResolutionService"/>. Moved verbatim from
/// <c>ClaimsController.ResolvePendedClaim</c> (and its helpers); see that
/// action for the contract.
/// </summary>
public sealed class ClaimResolutionService : IClaimResolutionService
{
    private readonly IClaimRepository _claimRepository;
    private readonly IAiExaminationAuditRepository _auditRepository;
    private readonly IClaimEventPublisher _eventPublisher;
    private readonly IClaimVersionEventPublisher _versionEventPublisher;
    private readonly IConfiguration _configuration;
    private readonly ICurrentActor _actor;
    private readonly ILogger _logger;

    private readonly IClaimApprovalReadjudicator? _approvalReadjudicator;
    private readonly IAccumulatorOutboxProcessor? _accumulatorOutbox;
    private readonly IAccumulatorCommitLookup? _accumulatorCommitLookup;

    /// <summary>
    /// Work-management notification (claims.work.v1). Optional and
    /// fire-and-forget: a resolution never waits on, or fails because of,
    /// work-management-service; the work item completes eventually (event, or
    /// work-management's reconcile).
    /// </summary>
    private readonly IClaimWorkEventPublisher? _workEvents;

    /// <summary>Which edits a reviewer may override (claim-review-workbench §2.3).</summary>
    private readonly EditOverridePolicy _editOverridePolicy;

    /// <summary>Override reasons and manual denials are also written as Decision notes. Optional.</summary>
    private readonly IClaimNoteRepository? _claimNotes;

    public ClaimResolutionService(
        IClaimRepository claimRepository,
        IAiExaminationAuditRepository auditRepository,
        IClaimEventPublisher eventPublisher,
        IClaimVersionEventPublisher versionEventPublisher,
        IConfiguration configuration,
        ICurrentActor actor,
        ILogger<ClaimResolutionService> logger,
        IClaimApprovalReadjudicator? approvalReadjudicator = null,
        IAccumulatorOutboxProcessor? accumulatorOutbox = null,
        IAccumulatorCommitLookup? accumulatorCommitLookup = null,
        IClaimWorkEventPublisher? workEvents = null,
        EditOverridePolicy? editOverridePolicy = null,
        IClaimNoteRepository? claimNotes = null)
        : this(claimRepository, auditRepository, eventPublisher, versionEventPublisher, configuration, actor,
            (ILogger)logger, approvalReadjudicator, accumulatorOutbox, accumulatorCommitLookup, workEvents,
            editOverridePolicy, claimNotes)
    {
    }

    /// <summary>
    /// For <c>ClaimsController</c>'s direct-construction constructor, which
    /// passes its own logger so the log output is unchanged.
    /// </summary>
    internal ClaimResolutionService(
        IClaimRepository claimRepository,
        IAiExaminationAuditRepository auditRepository,
        IClaimEventPublisher eventPublisher,
        IClaimVersionEventPublisher versionEventPublisher,
        IConfiguration configuration,
        ICurrentActor actor,
        ILogger logger,
        IClaimApprovalReadjudicator? approvalReadjudicator,
        IAccumulatorOutboxProcessor? accumulatorOutbox,
        IAccumulatorCommitLookup? accumulatorCommitLookup,
        IClaimWorkEventPublisher? workEvents,
        EditOverridePolicy? editOverridePolicy,
        IClaimNoteRepository? claimNotes)
    {
        _workEvents = workEvents;
        _editOverridePolicy = editOverridePolicy ?? EditOverridePolicy.Default;
        _claimNotes = claimNotes;
        _accumulatorOutbox = accumulatorOutbox;
        _accumulatorCommitLookup = accumulatorCommitLookup;
        _approvalReadjudicator = approvalReadjudicator;
        _claimRepository = claimRepository;
        _auditRepository = auditRepository;
        _eventPublisher = eventPublisher;
        _versionEventPublisher = versionEventPublisher;
        _configuration = configuration;
        _actor = actor;
        _logger = logger;
    }

    // Result constructors named like the ControllerBase helpers the moved
    // code used, so each return reads as it did in the controller.
    private static ClaimResolutionResult Ok(Claim claim) => new ClaimResolutionResult.Resolved(claim);
    private static ClaimResolutionResult Accepted(object body) => new ClaimResolutionResult.AwaitingSecondApproval(body);
    private static ClaimResolutionResult BadRequest(string message) => new ClaimResolutionResult.Invalid(message);
    private static ClaimResolutionResult NotFound() => new ClaimResolutionResult.ClaimNotFound();
    private static ClaimResolutionResult Conflict(object body) => new ClaimResolutionResult.Conflict(body);
    private static ClaimResolutionResult UnprocessableEntity(object body) => new ClaimResolutionResult.Unprocessable(body);
    private static ClaimResolutionResult Problem(int statusCode, string title, string detail) =>
        new ClaimResolutionResult.Problem(statusCode, title, detail);

    /// <summary>
    /// How long one examiner resolution may hold a claim, re-adjudication
    /// included. Generous, because the final write is fenced on the lock
    /// token: a resolver whose lock expired and was taken over cannot
    /// finalize (round-3 verification, L6).
    /// </summary>
    internal static readonly TimeSpan ResolutionLockDuration = TimeSpan.FromMinutes(10);

    /// <summary>Default lifetime of a first approval waiting for a second approver.</summary>
    internal static readonly TimeSpan DefaultSecondApprovalTtl = TimeSpan.FromHours(72);

    private TimeSpan SecondApprovalTtl =>
        double.TryParse(_configuration["Claims:SecondApprovalTtlHours"], System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var hours) && hours > 0
            ? TimeSpan.FromHours(hours)
            : DefaultSecondApprovalTtl;

    /// <inheritdoc />
    public async Task<ClaimResolutionResult> ResolveAsync(
        string claimId,
        ResolvePendedClaimRequest request,
        Func<string> getTenantId,
        string traceIdentifier,
        CancellationToken requestAborted)
    {
        // ── validate every input before any side effect (L7) ─────────────
        // An examiner's decision is the non-final adjudicated outcome: Pay
        // (Approved) or Deny. The payment cycle finalizes it (Paid / Denied).
        // "Denied" stays accepted for existing callers and means Deny here.
        if (ParseExaminerDisposition(request.Disposition) is not { } disposition)
        {
            return BadRequest("disposition must be Approved (Pay) or Denied (Deny)");
        }

        var validAgreements = new[] { "Accepted", "Modified", "Overridden" };
        if (!string.IsNullOrWhiteSpace(request.AiExaminerAgreement) && !validAgreements.Contains(request.AiExaminerAgreement))
        {
            return BadRequest($"aiExaminerAgreement must be one of: {string.Join(", ", validAgreements)}");
        }

        var claim = await _claimRepository.GetByIdAsync(claimId);
        if (claim == null) return NotFound();
        if (RefuseUnresolvable(claim) is { } refusal) return refusal;

        if (disposition == ClaimStatus.Approved && claim.PendDetails is not null
            && string.IsNullOrWhiteSpace(request.PendFingerprint))
        {
            return BadRequest("pendFingerprint is required to approve: the fingerprint of the pends you reviewed " +
                              "(pendDetails.fingerprint / the work-queue item's pendFingerprint).");
        }

        // The examiner is the authenticated caller; a body-supplied
        // ExaminerUserId is ignored.
        var examinerUserId = _actor.UserId;
        var tenantId = getTenantId();
        var hasReason = !string.IsNullOrWhiteSpace(request.Reason);
        var canOverride = _actor.HasPermission(ClaimsPermissions.OverrideApprove);

        if (!string.IsNullOrWhiteSpace(request.PendFingerprint)
            && !string.Equals(request.PendFingerprint, PendDetails.ComputeFingerprint(claim.PendDetails), StringComparison.Ordinal))
        {
            return PendsChanged(claim);
        }

        // Edit-level decisions: validated against the pend and the override
        // policy before anything happens.
        var (editDispositions, manualDenials, editRefusal) =
            BuildEditDecisions(claim, request, disposition, examinerUserId, tenantId);
        if (editRefusal is not null) return editRefusal;

        // payerSequence: checked, never clamped.
        if (request.PayerSequence is int sequence)
        {
            if (disposition != ClaimStatus.Approved)
                return BadRequest("payerSequence applies only to an approval.");
            if (!IsCobPend(claim.PendDetails))
                return BadRequest("payerSequence is accepted only for a claim pended for coordination of benefits (pend code COB).");
            var payers = claim.OtherPayers.Count + 1;
            if (sequence < 1 || sequence > payers)
                return BadRequest($"payerSequence must be between 1 and {payers} (the payers on this claim); got {sequence}.");

            var sbr01 = CloudHealthOffice.CobEngine.Domain.PayerResponsibility.ToSequence(claim.PayerResponsibilityCode);
            if (sbr01 is int stated && stated != sequence)
            {
                if (!canOverride)
                {
                    return Problem(statusCode: StatusCodes.Status403Forbidden,
                        title: "Payer-order override not permitted",
                        detail: $"payerSequence {sequence} disagrees with the 837 SBR01 ('{claim.PayerResponsibilityCode}' = {stated}); " +
                                $"that needs {ClaimsPermissions.OverrideApprove}.");
                }
                if (!hasReason)
                    return BadRequest($"payerSequence {sequence} disagrees with the 837 SBR01 ({stated}); a reason is required.");
            }
        }

        var needsSecondApprover = disposition == ClaimStatus.Approved
                                  && request.PayerSequence == 1
                                  && PriorPaidAmount(claim) > 0m;
        if (needsSecondApprover)
        {
            if (!canOverride)
            {
                return Problem(statusCode: StatusCodes.Status403Forbidden,
                    title: "Payer-order override not permitted",
                    detail: $"Pricing this claim as primary over a prior payment needs {ClaimsPermissions.OverrideApprove}.");
            }
            if (!hasReason)
                return BadRequest("Pricing this claim as primary over a prior payment needs a reason.");
        }

        // One resolution at a time (L10).
        var lockToken = Guid.NewGuid().ToString("N");
        var requestedAt = DateTime.UtcNow;
        if (!await _claimRepository.TryAcquireResolutionLockAsync(
                tenantId, claim.Id, lockToken, examinerUserId, requestedAt, ResolutionLockDuration, requestAborted))
        {
            return Conflict(new { error = $"Claim {claimId} is being resolved by another examiner, or is no longer Pended / Pay / Deny." });
        }

        try
        {
            claim = await _claimRepository.GetByIdAsync(claimId) ?? claim;
            if (RefuseUnresolvable(claim) is { } lockedRefusal) return lockedRefusal;
            // The phase the claim is resolved from: a Pay claim may already
            // have committed accumulators that a denial must take back.
            var sourceStatus = claim.Status;
            var reviewedPend = claim.PendDetails;
            var fingerprint = PendDetails.ComputeFingerprint(reviewedPend);
            if (!string.IsNullOrWhiteSpace(request.PendFingerprint)
                && !string.Equals(request.PendFingerprint, fingerprint, StringComparison.Ordinal))
            {
                return PendsChanged(claim);
            }

            var approverIds = new List<string> { examinerUserId };
            var approverReasons = new List<string?> { request.Reason };
            var firstRequestedAt = requestedAt;
            string? secondApprover = null;
            if (needsSecondApprover)
            {
                // A waiting first approval counts only for the same payer
                // order, the same pends, and until it expires (M5).
                var pending = claim.PendingExaminerApproval;
                var pendingValid = pending is not null
                                   && pending.PayerSequence == request.PayerSequence
                                   && string.Equals(pending.PendFingerprint, fingerprint, StringComparison.Ordinal)
                                   && pending.ExpiresAt > requestedAt;
                if (!pendingValid)
                {
                    claim.PendingExaminerApproval = new PendingExaminerApproval
                    {
                        RequestedBy = examinerUserId,
                        PayerSequence = request.PayerSequence,
                        Reason = request.Reason,
                        RequestedAt = requestedAt,
                        PendFingerprint = fingerprint,
                        ExpiresAt = requestedAt + SecondApprovalTtl,
                    };
                    claim.ResolutionLock = null;
                    claim.LastUpdatedBy = examinerUserId;
                    claim.LastUpdatedDate = requestedAt;
                    if (await _claimRepository.UpdateHoldingResolutionLockAsync(claim, lockToken, CancellationToken.None) is null)
                        return LostLock(claimId);
                    return Accepted(new
                    {
                        status = "awaiting-second-approval",
                        claimId,
                        payerSequence = request.PayerSequence,
                        requestedBy = examinerUserId,
                        expiresAt = claim.PendingExaminerApproval.ExpiresAt,
                        message = $"The 837 shows prior payers paid {PriorPaidAmount(claim):F2}; pricing this claim as primary " +
                                  "needs a second, different approver to approve it with the same payerSequence " +
                                  $"before {claim.PendingExaminerApproval.ExpiresAt:u}.",
                    });
                }
                if (string.Equals(pending!.RequestedBy, examinerUserId, StringComparison.Ordinal))
                {
                    return Conflict(new
                    {
                        error = "Pricing this claim as primary over a prior payment needs a second, different approver; " +
                                $"{examinerUserId} already approved it.",
                    });
                }
                approverIds = [pending.RequestedBy, examinerUserId];
                approverReasons = [pending.Reason, request.Reason];
                secondApprover = examinerUserId;
                firstRequestedAt = pending.RequestedAt;
            }

            IReadOnlyList<string> overridden = [];
            CloudHealthOffice.BenefitEngine.Models.AccumulatorCommit? accumulatorCommit = null;
            // Approval re-adjudicates the claim with the examiner's decision
            // applied, so the payment and the accumulators (deductible, OOP)
            // come from a fresh pricing. The re-run writes no accumulators;
            // they are committed below, after the final write.
            if (disposition == ClaimStatus.Approved)
            {
                if (_approvalReadjudicator is null)
                {
                    return Problem(
                        statusCode: StatusCodes.Status503ServiceUnavailable,
                        title: "Approval re-adjudication is unavailable",
                        detail: "An approved pended claim must be re-adjudicated before it finalizes.");
                }

                var rerun = await _approvalReadjudicator.ReadjudicateForApprovalAsync(
                    tenantId,
                    claim.Id,
                    new ExaminerApproval
                    {
                        ExaminerId = approverIds[0],
                        SecondApproverId = secondApprover,
                        Reason = request.Reason,
                        CorrelationId = Activity.Current?.TraceId.ToString() ?? traceIdentifier,
                        // The re-run's write is fenced on this lock (follow-up 1).
                        ResolutionLockToken = lockToken,
                        PayerSequence = request.PayerSequence,
                        PayerOrderOverrideAuthorized = canOverride && hasReason,
                        ReviewedPend = reviewedPend,
                        EditDispositions = editDispositions,
                        ManualLineDenials = manualDenials,
                    },
                    requestAborted);

                if (rerun.ResolutionLockLost
                    || (rerun.Outcome != ClaimAdjudicationOutcome.Pass
                        && !await HoldsResolutionLockAsync(tenantId, claimId, lockToken)))
                {
                    // The re-run outlived the lock and its write was refused:
                    // another examiner's resolution owns the claim now.
                    return LostLock(claimId);
                }
                if (rerun.Outcome != ClaimAdjudicationOutcome.Pass)
                {
                    var needsPayerOrder = IsCobPend(reviewedPend) && request.PayerSequence is null;
                    return Conflict(new
                    {
                        error = needsPayerOrder
                            ? "This claim is pended for coordination of benefits: approving it requires the payer order " +
                              "you confirmed (payerSequence: 1 = primary, 2 = secondary, 3+ = tertiary or later)."
                            : $"Re-adjudication for approval did not pass ({rerun.Outcome}); the claim was not approved. " +
                              "A pend not on the claim when it was reviewed needs a new review; a check that could " +
                              "not run (a service unavailable) is retried — try the approval again later.",
                        outcome = rerun.Outcome.ToString(),
                        reasons = rerun.UnresolvedReasons ?? (rerun.Reason is null ? [] : [rerun.Reason]),
                    });
                }
                overridden = rerun.OverriddenPends;
                accumulatorCommit = rerun.PreparedAccumulatorCommit;

                // The re-run persisted the new adjudication result (fenced on
                // this lock, so it is this resolver's); finalize that. The
                // final write below is fenced on the lock too.
                claim = await _claimRepository.GetByIdAsync(claimId) ?? claim;
            }
            else if (!await HoldsResolutionLockAsync(tenantId, claimId, lockToken))
            {
                // A resolver that lost its lock stops before deciding anything.
                return LostLock(claimId);
            }

            // From here the decision is made: finish it even if the caller
            // goes away (L9).
            var actedAt = DateTime.UtcNow;
            var denialReversal = disposition == ClaimStatus.Deny
                ? await DenialAccumulatorReversal.ForDenialAsync(
                    claim, sourceStatus, actedAt, _accumulatorCommitLookup, _logger, CancellationToken.None)
                : null;
            claim.LastUpdatedBy = examinerUserId;
            claim.Status = disposition;
            claim.VersionState = ClaimRepository.MapStatusToVersionState(disposition);
            claim.AdjudicatedDate = actedAt;
            claim.LastUpdatedDate = actedAt;
            claim.PendingExaminerApproval = null;
            claim.ResolutionLock = null;
            // Accumulator outbox, written by the same lock-fenced write that
            // makes the decision (and only if it lands): an approval owes the
            // commit its re-run prepared (it replaces the claim's own earlier
            // contribution in the store, so it counts once). A denial owes a
            // NON-terminal reversal of what the claim contributed, or nothing
            // if it contributed nothing (DenialAccumulatorReversal): Deny is
            // not final, so the claim may be reprocessed back to Pay and commit
            // again (a terminal reversal would fence the claim id and refuse
            // that commit). Any reversal still pending is dropped by an
            // approval: the new commit takes back the claim's own earlier
            // contribution itself.
            claim.PendingAccumulatorCommit = disposition == ClaimStatus.Approved && accumulatorCommit is not null
                ? AccumulatorOutboxItem.ForCommit(accumulatorCommit, actedAt)
                : null;
            claim.PendingAccumulatorReversal = denialReversal;
            // Re-Pay: the new commit is the claim's commit from now on. Any
            // reversal still queued for an older commit was cancelled above
            // in this same lock-fenced (ETag) write; one already in flight
            // is commit-scoped, so it can only be a no-op against this one.
            if (claim.PendingAccumulatorCommit is { } queuedCommit)
                claim.LatestAccumulatorCommitId = queuedCommit.Id;
            claim.ExaminerResolutions.Add(new ExaminerResolutionRecord
            {
                Disposition = disposition.ToString(),
                ApproverIds = approverIds,
                Reason = request.Reason,
                ApproverReasons = approverReasons,
                PendFingerprint = fingerprint,
                PayerSequence = request.PayerSequence,
                ReviewedPends = ExaminerApproval.ReviewedFrom(reviewedPend)
                    .Select(p => $"{p.Code}: {p.Reason}").ToList(),
                OverriddenPends = overridden.ToList(),
                EditDispositions = editDispositions.ToList(),
                ManualLineDenials = manualDenials.ToList(),
                RequestedAt = firstRequestedAt,
                ResolvedAt = actedAt,
            });
            if (!string.IsNullOrWhiteSpace(request.Reason))
            {
                claim.ClaimNotes = string.IsNullOrWhiteSpace(claim.ClaimNotes)
                    ? request.Reason
                    : $"{claim.ClaimNotes}\n{actedAt:yyyy-MM-dd HH:mm}: {request.Reason}";
            }

            if (claim.AiExamination is not null && !string.IsNullOrWhiteSpace(request.AiExaminerAgreement))
            {
                claim.AiExamination.ExaminerAgreement = request.AiExaminerAgreement;
                claim.AiExamination.ExaminerActedAt = actedAt;
                claim.AiExamination.ExaminerUserId = examinerUserId;
            }

            // Fenced on the lock token (L6): a resolver whose lock was taken
            // over cannot finalize, and nothing is published. The finalized
            // event is stored with this write (outbox).
            claim.AddPendingClaimEvent(_eventPublisher.PrepareClaimFinalized(claim, tenantId));
            var updated = await _claimRepository.UpdateHoldingResolutionLockAsync(claim, lockToken, CancellationToken.None);
            if (updated is null) return LostLock(claimId);

            await WriteDecisionNotesAsync(updated, editDispositions, manualDenials, examinerUserId, tenantId);

            // The decision is final (lock cleared: no other resolver can take
            // the claim now). Drive its accumulator outbox entry at once; a
            // failure, or a crash before this, leaves it on the claim for the
            // dispatcher (idempotent, with backoff).
            if (updated.PendingAccumulatorCommit is { } pendingCommit)
                await DriveAccumulatorOutboxAsync(updated, AccumulatorOutboxKind.Commit, pendingCommit.Id);
            if (updated.PendingAccumulatorReversal is { } pendingReversal)
                await DriveAccumulatorOutboxAsync(updated, AccumulatorOutboxKind.Reversal, pendingReversal.Id);

            var correlationId = Activity.Current?.TraceId.ToString() ?? traceIdentifier;
            await _versionEventPublisher.PublishVersionResolvedAsync(
                updated,
                disposition.ToString(),
                request.Reason,
                examinerUserId,
                correlationId,
                CancellationToken.None);

            if (claim.AiExamination is not null && !string.IsNullOrWhiteSpace(request.AiExaminerAgreement))
            {
                var auditUpdated = await _auditRepository.SetExaminerAgreementAsync(
                    claimId,
                    tenantId,
                    request.AiExaminerAgreement,
                    examinerUserId,
                    request.Reason);

                if (auditUpdated is null)
                {
                    _logger.LogWarning(
                        "No audit row found for claim {Id} when resolving pended claim; live AI feedback was persisted",
                        LogSanitizer.Sanitize(claimId));
                }
            }

            await _eventPublisher.PublishClaimFinalizedAsync(updated, tenantId);

            // The claim left Pended: its work item is done. A Pay / Deny claim
            // re-resolved here was not work (it never had an open item).
            if (sourceStatus == ClaimStatus.Pended && _workEvents is not null)
            {
                await _workEvents.PublishWorkResolvedAsync(
                    updated, tenantId, disposition.ToString(), examinerUserId, CancellationToken.None);
            }

            _logger.LogInformation(
                "Examiner {User} resolved pended claim {ClaimId} as {Disposition}: {Reason}",
                LogSanitizer.Sanitize(examinerUserId),
                LogSanitizer.Sanitize(claimId),
                disposition,
                LogSanitizer.Sanitize(request.Reason));

            return Ok(updated);
        }
        finally
        {
            // No-op once the final write cleared it; frees it on every other exit.
            await _claimRepository.ReleaseResolutionLockAsync(tenantId, claimId, lockToken, CancellationToken.None);
        }
    }

    /// <summary>
    /// The examiner disposition: "Approved" / "Pay" → <see cref="ClaimStatus.Approved"/>;
    /// "Denied" / "Deny" → <see cref="ClaimStatus.Deny"/> (never the finalized
    /// <see cref="ClaimStatus.Denied"/>: only the payment cycle sets that).
    /// Numeric values 5, 6 and 10 are accepted as before.
    /// </summary>
    internal static ClaimStatus? ParseExaminerDisposition(string? disposition) =>
        disposition?.Trim().ToLowerInvariant() switch
        {
            "approved" or "pay" or "5" => ClaimStatus.Approved,
            "denied" or "deny" or "6" or "10" => ClaimStatus.Deny,
            _ => null,
        };

    /// <summary>
    /// Null when an examiner may resolve the claim (Pended, Pay or Deny);
    /// 422 when it was finalized by the payment cycle or reversed (only an
    /// adjustment or a void changes it); 409 when it is not adjudicated yet.
    /// </summary>
    private static ClaimResolutionResult? RefuseUnresolvable(Claim claim)
    {
        if (ClaimStatusSemantics.IsFinalized(claim))
        {
            return UnprocessableEntity(new
            {
                error = ClaimStatusSemantics.FinalizedRefusal(claim),
                currentStatus = claim.Status,
                currentVersionState = claim.VersionState.ToString(),
            });
        }
        return ClaimStatusSemantics.IsReprocessable(claim.Status)
            ? null
            : Conflict($"Claim {claim.Id} is {claim.Status}, not Pended, Pay or Deny");
    }

    private static ClaimResolutionResult PendsChanged(Claim claim) => Conflict(new
    {
        error = "The claim's pends changed since you reviewed them (it was re-adjudicated). Review the current pends " +
                "and approve again.",
        pendFingerprint = PendDetails.ComputeFingerprint(claim.PendDetails),
        pendReasons = PendReasonsOf(claim),
    });

    /// <summary>
    /// The claim still holds this resolution lock, unexpired, and is still
    /// Pended (read now, immediately before a side effect).
    /// </summary>
    private Task<bool> HoldsResolutionLockAsync(string tenantId, string claimId, string lockToken) =>
        _claimRepository.HoldsResolutionLockAsync(tenantId, claimId, lockToken, DateTime.UtcNow, CancellationToken.None);

    private static ClaimResolutionResult LostLock(string claimId) => Conflict(new
    {
        error = $"The resolution of claim {claimId} took too long and another examiner's resolution took over; " +
                "nothing was finalized. Reload the claim.",
    });

    /// <summary>The claim is pended for COB: its pend code, or a COB reason added after it.</summary>
    private static bool IsCobPend(PendDetails? pend) =>
        ExaminerApproval.ReviewedFrom(pend).Any(p => string.Equals(p.Code, "COB", StringComparison.OrdinalIgnoreCase));

    /// <summary>What the 837's prior payers paid: 2320 AMT*D, else their 2430 SVD02 total.</summary>
    private static decimal PriorPaidAmount(Claim claim) =>
        claim.OtherPayers.Sum(p => p.PaidAmount ?? p.LineAdjudications.Sum(l => l.PaidAmount));

    /// <summary>One immediate outbox attempt (best effort; the dispatcher retries).</summary>
    private async Task DriveAccumulatorOutboxAsync(Claim claim, AccumulatorOutboxKind kind, string itemId)
    {
        if (_accumulatorOutbox is null) return;
        try
        {
            await _accumulatorOutbox.ProcessAsync(claim.TenantId, claim.Id, kind, itemId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Claim {ClaimId}: accumulator {Kind} not driven now; the outbox dispatcher retries it",
                LogSanitizer.Sanitize(claim.Id), kind);
        }
    }

    /// <summary>
    /// Validates the reviewer's edit decisions and manual line denials
    /// against the pend they reviewed and the override policy, and fills in
    /// every undecided edit as upheld (uphold by default). Returns a refusal
    /// when anything is invalid or not permitted.
    /// </summary>
    private (IReadOnlyList<EditDisposition> Dispositions, IReadOnlyList<ManualLineDenial> Denials, ClaimResolutionResult? Refusal)
        BuildEditDecisions(Claim claim, ResolvePendedClaimRequest request, ClaimStatus disposition, string examinerUserId, string tenantId)
    {
        if (request.EditDecisions is null && request.LineDenials is null)
            return ([], [], null);
        if (disposition != ClaimStatus.Approved)
            return ([], [], BadRequest("editDecisions and lineDenials apply only to an approval."));

        var now = DateTime.UtcNow;
        var edits = ClaimEditCatalog.Edits(claim.PendDetails);
        var byKey = edits.ToDictionary(e => e.EditKey, StringComparer.OrdinalIgnoreCase);
        var decided = new Dictionary<string, EditDisposition>(StringComparer.OrdinalIgnoreCase);

        foreach (var d in request.EditDecisions ?? [])
        {
            if (!byKey.TryGetValue(d.EditKey ?? string.Empty, out var edit))
                return ([], [], BadRequest($"Edit '{d.EditKey}' is not on this claim's pend."));
            if (decided.ContainsKey(edit.EditKey))
                return ([], [], BadRequest($"Edit '{d.EditKey}' is decided more than once."));
            if (!Enum.TryParse<EditDecision>(d.Decision, ignoreCase: true, out var decision))
                return ([], [], BadRequest($"decision for '{d.EditKey}' must be Uphold or Override."));

            string? level = null;
            if (decision == EditDecision.Override)
            {
                var rule = _editOverridePolicy.RuleFor(tenantId, claim.LineOfBusiness, edit.Category);
                if (rule.Level == OverrideLevel.Never)
                {
                    return ([], [], Problem(statusCode: StatusCodes.Status403Forbidden,
                        title: "Edit can't be overridden",
                        detail: $"{rule.Description}: the policy allows upholding it only ({edit.EditKey})."));
                }
                if (rule.Level == OverrideLevel.Supervisor && !_actor.HasPermission(ClaimsPermissions.OverrideEdit))
                {
                    return ([], [], Problem(statusCode: StatusCodes.Status403Forbidden,
                        title: "Override needs a supervisor",
                        detail: $"{rule.Description}: overriding it needs {ClaimsPermissions.OverrideEdit} ({edit.EditKey})."));
                }
                if ((rule.RequiresReason || rule.RequiresDocumentation) && string.IsNullOrWhiteSpace(d.Reason))
                    return ([], [], BadRequest($"Overriding '{edit.EditKey}' needs a reason."));
                level = rule.Level.ToString();
            }

            decided[edit.EditKey] = new EditDisposition
            {
                EditKey = edit.EditKey,
                EditType = edit.EditType,
                RuleId = edit.RuleId,
                LineNumbers = edit.LineNumbers.ToList(),
                Decision = decision,
                Reason = string.IsNullOrWhiteSpace(d.Reason) ? null : d.Reason.Trim(),
                PolicyLevel = level,
                DecidedBy = examinerUserId,
                DecidedAt = now,
            };
        }

        // Uphold by default: every edit the reviewer didn't touch stands, and
        // the record says a person looked at it.
        var dispositions = edits.Select(e => decided.TryGetValue(e.EditKey, out var explicitDecision)
            ? explicitDecision
            : new EditDisposition
            {
                EditKey = e.EditKey,
                EditType = e.EditType,
                RuleId = e.RuleId,
                LineNumbers = e.LineNumbers.ToList(),
                Decision = EditDecision.Uphold,
                UpheldByDefault = true,
                DecidedBy = examinerUserId,
                DecidedAt = now,
            }).ToList();

        var denials = new List<ManualLineDenial>();
        foreach (var l in request.LineDenials ?? [])
        {
            var line = claim.ClaimLines.FirstOrDefault(cl => cl.LineNumber == l.LineNumber);
            if (line is null)
                return ([], [], BadRequest($"Line {l.LineNumber} is not on this claim."));
            if (denials.Any(x => x.LineNumber == l.LineNumber))
                return ([], [], BadRequest($"Line {l.LineNumber} is denied more than once."));
            if (ReviewerDenialReasons.Find(l.GroupCode, l.Carc) is null)
                return ([], [], BadRequest($"{l.GroupCode}-{l.Carc} is not a reviewer denial reason; use one of: " +
                    string.Join(", ", ReviewerDenialReasons.All.Select(r => $"{r.GroupCode}-{r.Carc}"))));
            if (string.IsNullOrWhiteSpace(l.Reason))
                return ([], [], BadRequest($"Denying line {l.LineNumber} needs a reason."));
            if (l.Units is { } units && (units <= 0 || units > line.Units))
                return ([], [], BadRequest($"Units denied on line {l.LineNumber} must be between 0 and {line.Units}."));

            denials.Add(new ManualLineDenial
            {
                LineNumber = l.LineNumber,
                Units = l.Units,
                GroupCode = l.GroupCode.Trim().ToUpperInvariant(),
                Carc = l.Carc.Trim(),
                Rarc = string.IsNullOrWhiteSpace(l.Rarc) ? null : l.Rarc.Trim(),
                Reason = l.Reason.Trim(),
                DecidedBy = examinerUserId,
                DecidedAt = now,
            });
        }

        return (dispositions, denials, null);
    }

    /// <summary>
    /// Override reasons and manual denials double as Decision notes, so the
    /// claim's notes tell the story without opening the audit. Best effort:
    /// the decision is already recorded on the claim.
    /// </summary>
    private async Task WriteDecisionNotesAsync(
        Claim claim, IReadOnlyList<EditDisposition> dispositions, IReadOnlyList<ManualLineDenial> denials,
        string examinerUserId, string tenantId)
    {
        if (_claimNotes is null) return;
        var texts = dispositions
            .Where(d => d.Decision == EditDecision.Override)
            .Select(d => $"Overrode {d.RuleId} on line {string.Join(", ", d.LineNumbers)}: {d.Reason}")
            .Concat(denials.Select(d =>
                $"Denied line {d.LineNumber}{(d.Units is { } u ? $" ({u} unit(s))" : "")} with {d.GroupCode}-{d.Carc}: {d.Reason}"));
        foreach (var text in texts)
        {
            try
            {
                await _claimNotes.AppendAsync(new ClaimNote
                {
                    TenantId = tenantId,
                    ClaimId = claim.Id,
                    ClaimNumber = claim.ClaimNumber,
                    Category = ClaimNoteCategories.Decision,
                    Text = text,
                    AuthorId = examinerUserId,
                    AuthorName = _actor.DisplayName,
                    CreatedAt = DateTime.UtcNow,
                }, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not write a Decision note on claim {ClaimId}", claim.Id);
            }
        }
    }

    /// <summary>
    /// Every reason the claim is pended for, the routing one first:
    /// "{PendCode}: {PendReason}", then
    /// <see cref="PendDetails.AdditionalPendReasons"/> (e.g. NCCI failures on
    /// a claim pended for COB).
    /// </summary>
    internal static List<string> PendReasonsOf(Claim c)
    {
        var reasons = new List<string>();
        if (c.PendDetails is { } pend)
        {
            if (!string.IsNullOrWhiteSpace(pend.PendCode) || !string.IsNullOrWhiteSpace(pend.PendReason))
                reasons.Add($"{pend.PendCode}: {pend.PendReason}".Trim());
            reasons.AddRange(pend.AdditionalPendReasons ?? []);
        }
        return reasons;
    }
}
