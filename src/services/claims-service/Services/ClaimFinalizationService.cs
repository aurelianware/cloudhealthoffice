using ClaimsService.Models;
using ClaimsService.Repositories;

namespace ClaimsService.Services;

/// <summary>
/// Owns the Adjudicated → Paid transition (5.10) and the Paid/Adjusted →
/// Voided transition (5.12a, invoked by 5.12b ReversalRun) for a claim
/// version. The canonical 5.10 finalize path: payment-service issues a
/// <c>POST /api/claims/{id}/remittance</c> per claim during PaymentRun
/// execution; the controller delegates here so the lifecycle write is
/// idempotent, source-state-validated, and emits the
/// <c>ClaimVersionPaid</c> event into the Mongo version chain alongside
/// the existing Kafka <c>claims.finalized.v1</c> notification.
///
/// <para>
/// 5.12a extends this service with <see cref="VoidAsync"/> per Gap 1
/// ratification — keeping the version-event emission unified inside one
/// service rather than splitting into a sibling <c>IClaimVoidService</c>.
/// The Void transition is wired in 5.12a; actual invocation occurs in
/// 5.12b's <c>ReversalRunService</c> when the predecessor is reversed.
/// </para>
/// </summary>
public interface IClaimFinalizationService
{
    Task<ClaimFinalizationResult> FinalizeAsync(
        string claimId,
        ClaimFinalizationRequest request,
        string tenantId,
        string? actorId,
        string? correlationId,
        CancellationToken ct = default);

    /// <summary>
    /// Paid/Adjusted → Voided transition (5.12a; Gap 1 ratification).
    /// Idempotent: a second call against an already-Voided claim returns
    /// <see cref="ClaimVoidOutcome.AlreadyVoided"/>. Source-state guard
    /// rejects anything outside Paid/PartiallyPaid/Adjusted with
    /// <see cref="ClaimVoidOutcome.InvalidSourceState"/>.
    ///
    /// <para>
    /// Emits <c>ClaimVersionVoided</c> to the Mongo version chain and
    /// <c>claims.finalized.v1</c> with <c>FinalStatus="Reversed"</c>
    /// (the existing <c>ClaimEventPublisher.Status</c> Voided→"Reversed"
    /// mapping convention). Per Decision 16, the BP engine reversal
    /// path is the source of truth for accumulator un-application;
    /// this Kafka emit is observability/audit, not the accumulator
    /// trigger.
    /// </para>
    /// </summary>
    Task<ClaimVoidResult> VoidAsync(
        string claimId,
        ClaimVoidRequest request,
        string tenantId,
        string? actorId,
        string? correlationId,
        CancellationToken ct = default);
}

/// <summary>
/// Outcome discriminator on <see cref="ClaimFinalizationResult"/>. Lets
/// the controller map results to specific HTTP status codes without
/// catching exceptions on the happy path.
/// </summary>
public enum ClaimFinalizationOutcome
{
    /// <summary>Claim transitioned from Approved/PartiallyPaid → Paid; ClaimVersionPaid emitted; ClaimFinalized re-emitted.</summary>
    Finalized = 1,
    /// <summary>Claim already in Paid state with the same CheckNumber; no-op; no event re-emit.</summary>
    AlreadyFinalized = 2,
    /// <summary>Claim already in Paid state with a different CheckNumber; controller maps to 409 Conflict.</summary>
    Conflict = 3,
    /// <summary>Source claim is not in a Paid-eligible state; controller maps to 422 Unprocessable Entity.</summary>
    InvalidSourceState = 4,
    /// <summary>Claim id not found for the tenant; controller maps to 404 Not Found.</summary>
    NotFound = 5,
}

/// <summary>
/// Result envelope returned by <see cref="IClaimFinalizationService.FinalizeAsync"/>.
/// On success or AlreadyFinalized, <see cref="Claim"/> carries the
/// post-finalize claim version; on Conflict / InvalidSourceState,
/// <see cref="Claim"/> carries the existing claim so the controller can
/// surface its current state in the error body.
/// </summary>
public class ClaimFinalizationResult
{
    public ClaimFinalizationOutcome Outcome { get; init; }
    public Claim? Claim { get; init; }
    public string? Message { get; init; }

    public static ClaimFinalizationResult Finalized(Claim claim) =>
        new() { Outcome = ClaimFinalizationOutcome.Finalized, Claim = claim };

    public static ClaimFinalizationResult AlreadyFinalized(Claim claim) =>
        new() { Outcome = ClaimFinalizationOutcome.AlreadyFinalized, Claim = claim };

    public static ClaimFinalizationResult Conflict(Claim claim, string message) =>
        new() { Outcome = ClaimFinalizationOutcome.Conflict, Claim = claim, Message = message };

    public static ClaimFinalizationResult InvalidSourceState(Claim claim, string message) =>
        new() { Outcome = ClaimFinalizationOutcome.InvalidSourceState, Claim = claim, Message = message };

    public static ClaimFinalizationResult NotFound(string message) =>
        new() { Outcome = ClaimFinalizationOutcome.NotFound, Message = message };
}

/// <summary>
/// Inputs for the Adjudicated → Paid transition. CheckNumber and
/// PaymentDate flow into <c>AdjudicationResult.CheckNumber</c> and
/// <c>AdjudicationResult.PaymentDate</c>; PayerPayment overrides the
/// existing <c>AdjudicationResult.PayerPayment</c> only when supplied
/// (per-claim PaymentRun amounts win over pre-adjudication estimates).
/// EdiControlNumber, when supplied, flows into
/// <c>Claim.EDI835ControlNumber</c> as part of the same finalize
/// write — keeping both updates inside the single non-terminal
/// transition (the repository's terminal-state guard rejects any
/// follow-up write once <c>VersionState=Paid</c>).
/// </summary>
public class ClaimFinalizationRequest
{
    public string CheckNumber { get; set; } = string.Empty;
    public DateTime PaymentDate { get; set; } = DateTime.UtcNow;
    public decimal? PayerPayment { get; set; }
    public string? PaymentRunId { get; set; }
    public string? EraEnvelopeId { get; set; }
    public string? EdiControlNumber { get; set; }
}

/// <summary>
/// Outcome discriminator on <see cref="ClaimVoidResult"/>. Lets the
/// caller (5.12b ReversalRun) map results to specific HTTP status codes.
/// </summary>
public enum ClaimVoidOutcome
{
    /// <summary>Claim transitioned to Voided; ClaimVersionVoided emitted; ClaimFinalized re-emitted with FinalStatus="Reversed".</summary>
    Voided = 1,
    /// <summary>Claim already Voided; idempotent no-op; no event re-emit.</summary>
    AlreadyVoided = 2,
    /// <summary>Source claim is not in a Void-eligible state (must be Paid/PartiallyPaid/Adjusted); maps to 422.</summary>
    InvalidSourceState = 3,
    /// <summary>Claim id not found for the tenant; maps to 404.</summary>
    NotFound = 4,
}

public class ClaimVoidRequest
{
    /// <summary>Operator-supplied reason for the void. Required for the audit trail.</summary>
    public string Reason { get; set; } = string.Empty;
    /// <summary>Optional 5.12b ReversalRun id correlation.</summary>
    public string? ReversalRunId { get; set; }
}

public class ClaimVoidResult
{
    public ClaimVoidOutcome Outcome { get; init; }
    public Claim? Claim { get; init; }
    public string? Message { get; init; }

    public static ClaimVoidResult Voided(Claim claim) =>
        new() { Outcome = ClaimVoidOutcome.Voided, Claim = claim };
    public static ClaimVoidResult AlreadyVoided(Claim claim) =>
        new() { Outcome = ClaimVoidOutcome.AlreadyVoided, Claim = claim };
    public static ClaimVoidResult InvalidSourceState(Claim claim, string message) =>
        new() { Outcome = ClaimVoidOutcome.InvalidSourceState, Claim = claim, Message = message };
    public static ClaimVoidResult NotFound(string message) =>
        new() { Outcome = ClaimVoidOutcome.NotFound, Message = message };
}

public class ClaimFinalizationService : IClaimFinalizationService
{
    private readonly IClaimRepository _claimRepository;
    private readonly IClaimVersionEventPublisher _versionEventPublisher;
    private readonly IClaimEventPublisher _kafkaEventPublisher;
    private readonly IClaimAdjustmentService _adjustmentService;
    private readonly ILogger<ClaimFinalizationService> _logger;

    public ClaimFinalizationService(
        IClaimRepository claimRepository,
        IClaimVersionEventPublisher versionEventPublisher,
        IClaimEventPublisher kafkaEventPublisher,
        IClaimAdjustmentService adjustmentService,
        ILogger<ClaimFinalizationService> logger)
    {
        _claimRepository = claimRepository;
        _versionEventPublisher = versionEventPublisher;
        _kafkaEventPublisher = kafkaEventPublisher;
        _adjustmentService = adjustmentService;
        _logger = logger;
    }

    public async Task<ClaimFinalizationResult> FinalizeAsync(
        string claimId,
        ClaimFinalizationRequest request,
        string tenantId,
        string? actorId,
        string? correlationId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(claimId))
            throw new ArgumentException("claimId is required", nameof(claimId));
        if (string.IsNullOrWhiteSpace(request.CheckNumber))
            throw new ArgumentException("CheckNumber is required", nameof(request));

        var claim = await _claimRepository.GetByIdAsync(claimId);
        if (claim == null)
        {
            _logger.LogWarning("Finalize requested for unknown claim {ClaimId}", Sanitize(claimId));
            return ClaimFinalizationResult.NotFound($"Claim {claimId} not found");
        }

        // Idempotency — the second call with the same CheckNumber is a
        // structural no-op (no event re-emit, no UpdateAsync — the
        // repository's terminal-state guard would throw on re-update).
        if (claim.Status == ClaimStatus.Paid)
        {
            var existingCheck = claim.AdjudicationResult?.CheckNumber;
            if (string.Equals(existingCheck, request.CheckNumber, StringComparison.Ordinal))
            {
                _logger.LogInformation(
                    "Claim {ClaimId} already finalized with CheckNumber {Check}; idempotent no-op",
                    Sanitize(claim.Id), Sanitize(request.CheckNumber));
                return ClaimFinalizationResult.AlreadyFinalized(claim);
            }

            _logger.LogWarning(
                "Claim {ClaimId} already finalized with CheckNumber {Existing}; refusing re-finalize with {Incoming}",
                Sanitize(claim.Id), Sanitize(existingCheck), Sanitize(request.CheckNumber));
            return ClaimFinalizationResult.Conflict(
                claim,
                $"Claim already paid under check {existingCheck}; cannot re-finalize with {request.CheckNumber}");
        }

        // Source-state validation — Phase 1 only supports
        // Approved/PartiallyPaid → Paid. Other transitions are deferred
        // to 5.12 (Adjustment Workflow).
        if (claim.Status is not (ClaimStatus.Approved or ClaimStatus.PartiallyPaid))
        {
            _logger.LogWarning(
                "Claim {ClaimId} cannot finalize from {Status}; only Approved/PartiallyPaid → Paid supported",
                Sanitize(claim.Id), claim.Status);
            return ClaimFinalizationResult.InvalidSourceState(
                claim,
                $"Claim must be Approved or PartiallyPaid to finalize; current status is {claim.Status}");
        }

        // Apply the Paid transition. Both Status (legacy operational
        // sub-state) and VersionState (5.1a chain) advance together; the
        // version stream is the audit-trail surface, the legacy Status
        // field is what the existing 22 controller endpoints and the
        // Kafka claims.finalized.v1 consumers read.
        var now = DateTime.UtcNow;
        claim.Status = ClaimStatus.Paid;
        claim.VersionState = ClaimVersionState.Paid;
        claim.PaidDate = now;
        claim.LastUpdatedDate = now;
        claim.LastUpdatedBy = actorId;

        claim.AdjudicationResult ??= new AdjudicationResult();
        claim.AdjudicationResult.CheckNumber = request.CheckNumber;
        claim.AdjudicationResult.PaymentDate = request.PaymentDate;
        if (request.PayerPayment.HasValue)
        {
            claim.AdjudicationResult.PayerPayment = request.PayerPayment.Value;
        }

        if (!string.IsNullOrEmpty(request.EdiControlNumber))
        {
            claim.EDI835ControlNumber = request.EdiControlNumber;
        }

        var updated = await _claimRepository.UpdateAsync(claim);

        // Version-chain event first (system-of-record); then the Kafka
        // notification for downstream consumers (accumulator, analytics).
        // Failures on the Kafka side are intentionally swallowed by the
        // publisher itself — the claim DB is truth, not the bus.
        try
        {
            await _versionEventPublisher.PublishVersionPaidAsync(updated, actorId, correlationId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to publish ClaimVersionPaid for claim {ClaimId}; claim already persisted",
                Sanitize(updated.Id));
        }

        await _kafkaEventPublisher.PublishClaimFinalizedAsync(updated, tenantId, ct);

        _logger.LogInformation(
            "Finalized claim {ClaimId} with CheckNumber {Check}; PaymentRun {PaymentRunId}, EraEnvelope {EnvelopeId}",
            Sanitize(updated.Id), Sanitize(request.CheckNumber),
            Sanitize(request.PaymentRunId), Sanitize(request.EraEnvelopeId));

        return ClaimFinalizationResult.Finalized(updated);
    }

    public async Task<ClaimVoidResult> VoidAsync(
        string claimId,
        ClaimVoidRequest request,
        string tenantId,
        string? actorId,
        string? correlationId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(claimId))
            throw new ArgumentException("claimId is required", nameof(claimId));
        if (string.IsNullOrWhiteSpace(tenantId))
            throw new ArgumentException("tenantId is required", nameof(tenantId));
        // Reason is required by the docstring + audit trail. Enforced
        // here so callers (5.12b ReversalRunService) cannot accidentally
        // emit empty-reason ClaimVersionVoided events.
        if (string.IsNullOrWhiteSpace(request.Reason))
            throw new ArgumentException("Reason is required for the audit trail", nameof(request));

        var claim = await _claimRepository.GetByIdAsync(claimId);
        if (claim == null || claim.TenantId != tenantId)
        {
            _logger.LogWarning("Void requested for unknown claim {ClaimId}", Sanitize(claimId));
            return ClaimVoidResult.NotFound($"Claim {claimId} not found");
        }

        if (claim.Status == ClaimStatus.Voided)
        {
            _logger.LogInformation(
                "Claim {ClaimId} already Voided; idempotent no-op", Sanitize(claim.Id));
            return ClaimVoidResult.AlreadyVoided(claim);
        }

        // Source-state guard. Phase 1 supports voiding from
        // Paid/PartiallyPaid (5.10 happy-path remittance terminal) or
        // from Adjusted (5.12a supersession-intermediate). Voiding from
        // Submitted/InAdjudication/Pended/Approved/Denied is rejected
        // (those have separate lifecycle paths).
        if (claim.Status is not (ClaimStatus.Paid or ClaimStatus.PartiallyPaid))
        {
            // Check VersionState too — supersession projection sets
            // VersionState=Adjusted while leaving Status=Paid; the
            // VersionState path captures that case.
            var voidFromAdjusted = claim.VersionState == ClaimVersionState.Adjusted;
            if (!voidFromAdjusted)
            {
                _logger.LogWarning(
                    "Claim {ClaimId} cannot void from Status={Status}/VersionState={VersionState}; only Paid/PartiallyPaid/Adjusted allowed",
                    Sanitize(claim.Id), claim.Status, claim.VersionState);
                return ClaimVoidResult.InvalidSourceState(
                    claim,
                    $"Claim must be Paid, PartiallyPaid, or Adjusted to void; current Status={claim.Status}, VersionState={claim.VersionState}");
            }
        }

        // Apply the Void transition via the projection bypass — the
        // regular UpdateAsync path rejects terminal-state mutations
        // (Paid is terminal). The projection-bypass exists precisely
        // for this lifecycle write.
        var voidedAt = DateTime.UtcNow;
        var ok = await _claimRepository.MarkVoidedProjectionAsync(
            tenantId, claim.Id, voidedAt, actorId, ct);
        if (!ok)
        {
            // Should not happen — we just read the row above. Surface as
            // not-found rather than crashing.
            _logger.LogWarning(
                "Void projection bypass for claim {ClaimId} matched 0 rows; treating as not-found",
                Sanitize(claim.Id));
            return ClaimVoidResult.NotFound($"Claim {claimId} not found (race or concurrent delete)");
        }

        // Refetch so the post-Void payload is what flows into events. If
        // the refetch returns null (concurrent delete between projection
        // write and refetch — extremely rare), fall back to the in-memory
        // claim BUT mutate it to the post-Void state so the version event
        // and Kafka emit reflect the actual transition rather than the
        // pre-Void Status/VersionState. The projection write succeeded;
        // the row exists in the post-Void state by definition.
        var updated = await _claimRepository.GetByIdAsync(claim.Id);
        if (updated == null)
        {
            claim.Status = ClaimStatus.Voided;
            claim.VersionState = ClaimVersionState.Voided;
            claim.LastUpdatedDate = voidedAt;
            claim.LastUpdatedBy = actorId;
            updated = claim;
            _logger.LogWarning(
                "Void refetch returned null for claim {ClaimId}; emitting events from in-memory post-Void state",
                Sanitize(claim.Id));
        }

        try
        {
            await _versionEventPublisher.PublishVersionVoidedAsync(updated, request.Reason, actorId, correlationId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to publish ClaimVersionVoided for claim {ClaimId}; void already persisted",
                Sanitize(updated.Id));
        }

        // Emit to Kafka — the existing ClaimEventPublisher.Status mapper
        // translates Voided → "Reversed" so accumulator-service +
        // analytics consumers see the canonical reversal signal. Per
        // Decision 16 the BP engine path is the source of truth for
        // accumulator un-application; this Kafka emit is observability
        // only.
        await _kafkaEventPublisher.PublishClaimFinalizedAsync(updated, tenantId, ct);

        // 5.12b Premise E — adjustment lifecycle callback. When the void
        // carries a ReversalRunId correlation, the in-flight adjustment
        // (whose PredecessorClaimId matches this voided claim and whose
        // Status is PendingReversal) transitions to Active. Operator-
        // initiated voids without a ReversalRunId are no-ops here.
        // Failure is non-blocking: the void has persisted and emitted; a
        // follow-up sweep can re-drive the lifecycle transition.
        if (!string.IsNullOrEmpty(request.ReversalRunId))
        {
            try
            {
                await _adjustmentService.MarkActiveOnReversalAsync(
                    tenantId, updated.Id, request.ReversalRunId, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Adjustment Active-transition callback failed for predecessor {ClaimId}; void already persisted",
                    Sanitize(updated.Id));
            }
        }

        _logger.LogInformation(
            "Voided claim {ClaimId}; reason='{Reason}'; ReversalRun={ReversalRunId}",
            Sanitize(updated.Id), Sanitize(request.Reason), Sanitize(request.ReversalRunId));

        return ClaimVoidResult.Voided(updated);
    }

    private static string Sanitize(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", string.Empty).Replace("\n", string.Empty);
}
