using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClaimsService.Models;
using ClaimsService.Models.Messaging;
using ClaimsService.Repositories;
using ClaimsService.Services.Adjudication;
using CloudHealthOffice.Infrastructure.Messaging;

namespace ClaimsService.Services;

/// <summary>
/// Operator-initiated adjustment workflow service (capability 5.12a).
/// Owns the supersession transition + new-version creation + Service Bus
/// emission that together make a Phase 1 adjustment. The 5.12b
/// payment-service ReversalRunService consumes the resulting
/// <c>PendingReversal</c> ClaimAdjustment rows to batch the predecessor
/// reversal + 835 reversal envelope.
///
/// <para>
/// Lifecycle ratified by Decision 18: <c>AwaitingReadjudication</c> →
/// <c>PendingReversal</c> → <c>Active</c>. The new version's pipeline
/// runs asynchronously via the existing Service Bus
/// <c>claim-version-events</c> subscription, so the service ends in
/// <c>AwaitingReadjudication</c>; the transition to
/// <c>PendingReversal</c> is owned by the orchestrator-finalize callback
/// path (5.12b) once the new version reaches Adjudicated/Paid.
/// </para>
///
/// <para>
/// Idempotency (Decision 6): the operator-supplied
/// <c>Idempotency-Key</c> request header is the dedup signal. Same key +
/// same body → return existing adjustment (200); same key + different
/// body → 409 Conflict; different key → create new adjustment.
/// </para>
///
/// <para>
/// AI examination semantics (Gap 6): the new version runs AI examination
/// fresh (predecessor's <see cref="Claim.AiExamination"/> snapshot is
/// ignored). The service zeroes
/// <see cref="AdapterClaim.AiExamination"/>,
/// <see cref="AdapterClaim.PendDetails"/>, and
/// <see cref="AdapterClaim.AdjudicationResult"/> on the corrected
/// payload before persistence so a stale predecessor signal cannot leak
/// through.
/// </para>
/// </summary>
public interface IClaimAdjustmentService
{
    Task<ClaimAdjustmentResult> CreateAdjustmentAsync(
        string predecessorClaimId,
        ClaimAdjustmentRequest request,
        string idempotencyKey,
        string tenantId,
        string actorId,
        string? correlationId,
        CancellationToken ct = default);

    /// <summary>
    /// Orchestrator-finalize callback (5.12b Premise A). Invoked by
    /// <c>ClaimAdjudicationOrchestrator</c> after a new claim version's
    /// pipeline emits <c>ClaimVersionAdjudicated</c>. If a tenant has an
    /// in-flight <see cref="ClaimAdjustment"/> whose
    /// <see cref="ClaimAdjustment.NewClaimId"/> matches the finalized
    /// version, transitions the adjustment lifecycle:
    /// <list type="bullet">
    ///   <item><description><see cref="ClaimAdjudicationOutcome.Pass"/> / <see cref="ClaimAdjudicationOutcome.Deny"/> → <see cref="ClaimAdjustmentStatus.PendingReversal"/> (terminal pipeline state; predecessor still has accumulator impact pending ReversalRun unwind).</description></item>
    ///   <item><description><see cref="ClaimAdjudicationOutcome.Reject"/> → <see cref="ClaimAdjustmentStatus.Failed"/> (pipeline pre-adjudication rejection — operator triage).</description></item>
    ///   <item><description><see cref="ClaimAdjudicationOutcome.Pend"/> → no transition (still awaiting human review).</description></item>
    /// </list>
    /// No-op when no matching adjustment exists (handles the common case
    /// of fresh non-adjustment submissions). Idempotent: re-invocation
    /// against an already-PendingReversal/Failed adjustment is a logged no-op.
    /// </summary>
    Task OnNewVersionFinalizedAsync(
        string tenantId,
        string newClaimId,
        ClaimAdjudicationOutcome outcome,
        CancellationToken ct = default);

    /// <summary>
    /// Reversal-completion callback (5.12b Premise E). Invoked by
    /// <c>ClaimFinalizationService.VoidAsync</c> when a void with non-null
    /// <c>ClaimVoidRequest.ReversalRunId</c> succeeds. Looks up the
    /// in-flight adjustment whose
    /// <see cref="ClaimAdjustment.PredecessorClaimId"/> matches the voided
    /// claim and transitions it from <see cref="ClaimAdjustmentStatus.PendingReversal"/>
    /// to <see cref="ClaimAdjustmentStatus.Active"/>; sets
    /// <see cref="ClaimAdjustment.ReversalCompletedAt"/> and
    /// <see cref="ClaimAdjustment.ReversalRunId"/>. No-op when no matching
    /// adjustment exists (e.g. operator-initiated void without a
    /// ReversalRun) or when the adjustment is already Active (idempotent).
    /// </summary>
    Task MarkActiveOnReversalAsync(
        string tenantId,
        string predecessorClaimId,
        string reversalRunId,
        CancellationToken ct = default);
}

/// <summary>
/// Outcome discriminator on <see cref="ClaimAdjustmentResult"/>. Lets
/// the controller map results to specific HTTP status codes without
/// re-deriving disposition from message contents.
/// </summary>
public enum ClaimAdjustmentOutcome
{
    /// <summary>New adjustment created; predecessor superseded; new version persisted; pipeline triggered. 201.</summary>
    Created = 1,
    /// <summary>Same Idempotency-Key + same RequestHash → return existing row. 200.</summary>
    AlreadyExists = 2,
    /// <summary>Same Idempotency-Key + different RequestHash → 409.</summary>
    IdempotencyConflict = 3,
    /// <summary>Predecessor not in Paid/Denied/PartiallyPaid → 422 (Decision 12).</summary>
    InvalidSourceState = 4,
    /// <summary>Predecessor itself is an adjustment of a prior version → 422 (Decision 11; depth=1 only in Phase 1).</summary>
    DepthLimitExceeded = 5,
    /// <summary>An in-flight adjustment already exists for the chain → 409.</summary>
    ConflictingAdjustment = 6,
    /// <summary>Predecessor claim id not found for the tenant → 404.</summary>
    PredecessorNotFound = 7,
    /// <summary>Submission service rejected the corrected payload (validation or vendor adapter NotImplemented). 400 / 501; check <see cref="ClaimAdjustmentResult.SubmissionFailureKind"/>.</summary>
    SubmissionFailed = 8,
}

public class ClaimAdjustmentResult
{
    public ClaimAdjustmentOutcome Outcome { get; init; }
    public ClaimAdjustment? Adjustment { get; init; }
    public AdapterClaim? NewVersion { get; init; }
    public Claim? Predecessor { get; init; }
    public string? Message { get; init; }

    /// <summary>Carried through on <see cref="ClaimAdjustmentOutcome.SubmissionFailed"/> so the controller can surface field-level detail.</summary>
    public IReadOnlyList<ValidationError> SubmissionErrors { get; init; } = Array.Empty<ValidationError>();

    /// <summary>
    /// Discriminates the underlying submission failure kind so the
    /// controller can map <see cref="ClaimSubmissionFailureKind.Validation"/>
    /// to 400 and <see cref="ClaimSubmissionFailureKind.NotImplemented"/>
    /// to 501. Null for non-submission failure outcomes and for
    /// non-submission-driven failure paths
    /// (e.g. supersession write failure).
    /// </summary>
    public ClaimSubmissionFailureKind? SubmissionFailureKind { get; init; }

    public static ClaimAdjustmentResult Created(ClaimAdjustment adjustment, AdapterClaim newVersion) =>
        new() { Outcome = ClaimAdjustmentOutcome.Created, Adjustment = adjustment, NewVersion = newVersion };

    public static ClaimAdjustmentResult AlreadyExists(ClaimAdjustment existing) =>
        new() { Outcome = ClaimAdjustmentOutcome.AlreadyExists, Adjustment = existing };

    public static ClaimAdjustmentResult IdempotencyConflict(ClaimAdjustment existing, string message) =>
        new() { Outcome = ClaimAdjustmentOutcome.IdempotencyConflict, Adjustment = existing, Message = message };

    public static ClaimAdjustmentResult InvalidSourceState(Claim predecessor, string message) =>
        new() { Outcome = ClaimAdjustmentOutcome.InvalidSourceState, Predecessor = predecessor, Message = message };

    public static ClaimAdjustmentResult DepthLimitExceeded(Claim predecessor, string message) =>
        new() { Outcome = ClaimAdjustmentOutcome.DepthLimitExceeded, Predecessor = predecessor, Message = message };

    public static ClaimAdjustmentResult ConflictingAdjustment(ClaimAdjustment existing, string message) =>
        new() { Outcome = ClaimAdjustmentOutcome.ConflictingAdjustment, Adjustment = existing, Message = message };

    public static ClaimAdjustmentResult PredecessorNotFound(string message) =>
        new() { Outcome = ClaimAdjustmentOutcome.PredecessorNotFound, Message = message };

    public static ClaimAdjustmentResult SubmissionFailed(
        string message,
        IReadOnlyList<ValidationError> errors,
        ClaimSubmissionFailureKind? failureKind = null) =>
        new()
        {
            Outcome = ClaimAdjustmentOutcome.SubmissionFailed,
            Message = message,
            SubmissionErrors = errors,
            SubmissionFailureKind = failureKind,
        };
}

public class ClaimAdjustmentService : IClaimAdjustmentService
{
    private readonly IClaimRepository _claimRepository;
    private readonly IClaimAdjustmentRepository _adjustmentRepository;
    private readonly IClaimSubmissionService _submissionService;
    private readonly IClaimVersionEventPublisher _versionEventPublisher;
    private readonly IMessageBus _messageBus;
    private readonly ILogger<ClaimAdjustmentService> _logger;

    public ClaimAdjustmentService(
        IClaimRepository claimRepository,
        IClaimAdjustmentRepository adjustmentRepository,
        IClaimSubmissionService submissionService,
        IClaimVersionEventPublisher versionEventPublisher,
        IMessageBus messageBus,
        ILogger<ClaimAdjustmentService> logger)
    {
        _claimRepository = claimRepository;
        _adjustmentRepository = adjustmentRepository;
        _submissionService = submissionService;
        _versionEventPublisher = versionEventPublisher;
        _messageBus = messageBus;
        _logger = logger;
    }

    public async Task<ClaimAdjustmentResult> CreateAdjustmentAsync(
        string predecessorClaimId,
        ClaimAdjustmentRequest request,
        string idempotencyKey,
        string tenantId,
        string actorId,
        string? correlationId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(predecessorClaimId))
            throw new ArgumentException("predecessorClaimId is required", nameof(predecessorClaimId));
        if (string.IsNullOrWhiteSpace(idempotencyKey))
            throw new ArgumentException("idempotencyKey is required", nameof(idempotencyKey));
        if (string.IsNullOrWhiteSpace(tenantId))
            throw new ArgumentException("tenantId is required", nameof(tenantId));
        if (string.IsNullOrWhiteSpace(actorId))
            throw new ArgumentException("actorId is required", nameof(actorId));

        // Step 1 — idempotency check. Operator may retry the same request
        // (network blip, double-click, replay). Same key + same body → return
        // the existing adjustment with 200; same key + different body → 409.
        var requestHash = ComputeRequestHash(predecessorClaimId, request);
        var existing = await _adjustmentRepository.GetByIdempotencyKeyAsync(tenantId, idempotencyKey, ct);
        if (existing != null)
        {
            if (string.Equals(existing.RequestHash, requestHash, StringComparison.Ordinal))
            {
                _logger.LogInformation(
                    "Adjustment {AdjustmentId} replay (same Idempotency-Key {Key}, same body); returning existing",
                    Sanitize(existing.Id), Sanitize(idempotencyKey));
                return ClaimAdjustmentResult.AlreadyExists(existing);
            }

            _logger.LogWarning(
                "Adjustment Idempotency-Key {Key} replay with different body; returning 409",
                Sanitize(idempotencyKey));
            return ClaimAdjustmentResult.IdempotencyConflict(
                existing,
                $"Idempotency-Key '{idempotencyKey}' already used with a different request body");
        }

        // Step 2 — predecessor lookup + source-state validation (Decision 12).
        var predecessor = await _claimRepository.GetByIdAsync(predecessorClaimId);
        if (predecessor == null || predecessor.TenantId != tenantId)
        {
            _logger.LogInformation(
                "Adjustment requested for unknown predecessor {ClaimId}",
                Sanitize(predecessorClaimId));
            return ClaimAdjustmentResult.PredecessorNotFound(
                $"Predecessor claim {predecessorClaimId} not found for tenant {tenantId}");
        }

        if (predecessor.Status is not (ClaimStatus.Paid or ClaimStatus.Denied or ClaimStatus.PartiallyPaid))
        {
            _logger.LogWarning(
                "Adjustment rejected for {ClaimId}: source state {Status} not in Paid/Denied/PartiallyPaid",
                Sanitize(predecessor.Id), predecessor.Status);
            return ClaimAdjustmentResult.InvalidSourceState(
                predecessor,
                $"Predecessor must be Paid, Denied, or PartiallyPaid to adjust; current status is {predecessor.Status}");
        }

        // Step 3 — depth=1 check (Decision 11). Predecessor's
        // PredecessorVersionId must be null (i.e. predecessor must be a
        // first-generation claim, not itself an adjustment).
        if (!string.IsNullOrEmpty(predecessor.PredecessorVersionId))
        {
            _logger.LogWarning(
                "Adjustment rejected for {ClaimId}: predecessor is itself an adjustment (depth>1 deferred to Phase 2)",
                Sanitize(predecessor.Id));
            return ClaimAdjustmentResult.DepthLimitExceeded(
                predecessor,
                "Adjustment-of-adjustment is not supported in Phase 1; predecessor must be a first-generation claim");
        }

        // Step 4 — chain-lock check. The Mongo unique index on
        // (TenantId, ClaimVersionId) enforces depth=1 globally; this
        // explicit pre-check returns a clean 409 before we attempt the
        // insert (better operator UX than a swallowed duplicate-key error).
        var chainKey = string.IsNullOrEmpty(predecessor.ClaimVersionId) ? predecessor.Id : predecessor.ClaimVersionId;
        var inflight = await _adjustmentRepository.GetByClaimVersionIdAsync(tenantId, chainKey, ct);
        if (inflight != null)
        {
            _logger.LogWarning(
                "Adjustment rejected for chain {ChainKey}: in-flight adjustment {AdjustmentId} already exists",
                Sanitize(chainKey), Sanitize(inflight.Id));
            return ClaimAdjustmentResult.ConflictingAdjustment(
                inflight,
                $"Chain {chainKey} already has an in-flight adjustment {inflight.Id} in status {inflight.Status}");
        }

        // Step 5 — acquire the chain lock by inserting a placeholder
        // ClaimAdjustment row BEFORE supersession + submission. The
        // unique index on (TenantId, ClaimVersionId) is the
        // serialization point: a concurrent request for the same chain
        // collides at insert time and gets a clean 409 with no
        // side effects. Without this early insert, two requests could
        // both pass the in-memory inflight check, both supersede +
        // submit, and only THEN one would lose at the duplicate-key
        // step — by which point the loser already has a new version
        // persisted and supersession events emitted. NewClaimId is
        // empty until step 7's update.
        var adjustment = new ClaimAdjustment
        {
            TenantId = tenantId,
            ClaimVersionId = chainKey,
            PredecessorClaimId = predecessor.Id,
            PredecessorVersionId = predecessor.Id,
            NewClaimId = string.Empty,
            AdjustmentReason = request.AdjustmentReason,
            Notes = request.Notes,
            Status = ClaimAdjustmentStatus.AwaitingReadjudication,
            IdempotencyKey = idempotencyKey,
            RequestHash = requestHash,
            CreatedBy = actorId,
            CreatedAt = DateTime.UtcNow,
        };

        try
        {
            await _adjustmentRepository.CreateAsync(adjustment, ct);
        }
        catch (MongoDB.Driver.MongoWriteException ex) when (ex.WriteError?.Category == MongoDB.Driver.ServerErrorCategory.DuplicateKey)
        {
            // Lost the race for this chain or idempotency key. Re-fetch
            // and surface the appropriate 409 — supersession + version
            // events have NOT yet fired, so this is a clean rejection.
            var raceExisting = await _adjustmentRepository.GetByIdempotencyKeyAsync(tenantId, idempotencyKey, ct);
            if (raceExisting != null)
            {
                return string.Equals(raceExisting.RequestHash, requestHash, StringComparison.Ordinal)
                    ? ClaimAdjustmentResult.AlreadyExists(raceExisting)
                    : ClaimAdjustmentResult.IdempotencyConflict(raceExisting,
                        $"Idempotency-Key '{idempotencyKey}' already used with a different request body");
            }
            var raceChain = await _adjustmentRepository.GetByClaimVersionIdAsync(tenantId, chainKey, ct);
            if (raceChain != null)
            {
                return ClaimAdjustmentResult.ConflictingAdjustment(raceChain,
                    $"Chain {chainKey} already has an in-flight adjustment {raceChain.Id} in status {raceChain.Status}");
            }
            throw;
        }

        // Step 6 — prepare the corrected claim payload. Override
        // identity-bearing fields so the new version is a fresh row that
        // joins the existing chain. Per Gap 6, zero stale signals from the
        // predecessor (AI rationale, pend reasons, prior adjudication) so
        // the pipeline runs the corrected facts cleanly.
        var corrected = request.CorrectedClaim;
        corrected.TenantId = tenantId;
        corrected.Id = string.Empty;                            // CreateAsync generates a fresh id
        corrected.ClaimVersionId = chainKey;                    // Stays on the same chain
        corrected.VersionNumber = predecessor.VersionNumber + 1;
        corrected.VersionState = ClaimVersionState.Submitted;
        corrected.PredecessorVersionId = predecessor.Id;        // Per-row id of the version being amended
        corrected.PublishedAt = null;
        corrected.PublishedBy = null;
        corrected.SupersededAt = null;
        corrected.SupersededByVersionId = null;
        corrected.SubmittedDate = DateTime.UtcNow;
        corrected.AdjudicatedDate = null;
        corrected.PaidDate = null;
        corrected.AdjudicationResult = null;
        corrected.PendDetails = null;
        corrected.AiExamination = null;                          // Gap 6 — fresh AI examination
        corrected.Status = ClaimStatus.Submitted;
        corrected.CreatedDate = DateTime.UtcNow;
        corrected.LastUpdatedDate = DateTime.UtcNow;
        corrected.CreatedBy = actorId;
        corrected.LastUpdatedBy = actorId;
        corrected.EDI835ControlNumber = null;                    // Reversal/payment control numbers belong to the new version's lifecycle

        // Step 6 — submit the new version through the canonical submission
        // path. SubmitAsync emits ClaimVersionSubmitted to the audit chain
        // AND ClaimVersionSubmittedMessage to the Service Bus topic, which
        // triggers the existing adjudication orchestrator subscription.
        // Re-adjudication is just a normal pipeline run on a new row.
        var submission = await _submissionService.SubmitAsync(corrected, tenantId, actorId, correlationId, ct);
        if (!submission.Success)
        {
            var errSummary = string.Join("; ", submission.Errors.Select(e => $"{e.Field}:{e.Code}"));
            _logger.LogWarning(
                "Adjustment submission failed for predecessor {ClaimId} ({FailureKind}): {ErrorSummary}",
                Sanitize(predecessor.Id), submission.FailureKind, Sanitize(errSummary));
            // Release the chain lock — no version was created, no
            // supersession occurred. Operator can retry against a clean
            // chain. Failure to delete is logged but doesn't change the
            // returned outcome (a stale Failed-state row would block
            // the chain; that's surfaced via support).
            await TryReleaseChainLockAsync(adjustment, ct);
            // Preserve the failure-kind discriminator so the controller
            // can map Validation → 400 and NotImplemented → 501. The
            // message text differs by kind so the operator-facing copy
            // matches the disposition.
            var message = submission.FailureKind == ClaimSubmissionFailureKind.NotImplemented
                ? "Adapter for this tenant does not implement claim submission"
                : "Corrected claim failed validation";
            return ClaimAdjustmentResult.SubmissionFailed(
                message,
                submission.Errors,
                submission.FailureKind);
        }

        var newVersion = submission.Claim!;

        // Step 7 — supersede the predecessor via the projection-bypass
        // write (the regular UpdateAsync path rejects terminal-state
        // mutations). Patches SupersededAt + SupersededByVersionId +
        // VersionState=Adjusted on the predecessor row. Status field is
        // NOT touched here — predecessor stays Paid until 5.12b's
        // ReversalRun explicitly transitions it to Voided via VoidAsync.
        var supersededAt = DateTime.UtcNow;
        var supersedeOk = await _claimRepository.MarkSupersededProjectionAsync(
            tenantId, predecessor.Id, newVersion.Id, supersededAt, actorId, ct);
        if (!supersedeOk)
        {
            _logger.LogError(
                "Failed to supersede predecessor {ClaimId} after new version {NewClaimId} was created; manual triage required",
                Sanitize(predecessor.Id), Sanitize(newVersion.Id));
            // Release the chain lock so retry is possible. The new
            // version row is left behind on the audit chain (operator
            // can re-trigger supersession via a fresh adjustment with
            // a new Idempotency-Key, or operations can intervene
            // manually). Surface as SubmissionFailed so the caller
            // sees a non-2xx.
            await TryReleaseChainLockAsync(adjustment, ct);
            return ClaimAdjustmentResult.SubmissionFailed(
                "Predecessor supersession failed after new version was created; contact operations",
                Array.Empty<ValidationError>());
        }

        // Step 8 — refetch the superseded predecessor so the version-event
        // payload carries the post-supersession state (SupersededAt,
        // SupersededByVersionId, VersionState=Adjusted). Without the
        // refetch the event payload would still show the pre-supersession
        // snapshot.
        var supersededPredecessor = await _claimRepository.GetByIdAsync(predecessor.Id) ?? predecessor;

        // Step 9 — emit the version-chain events. Two distinct events:
        // (a) ClaimVersionSuperseded for the audit/lineage signal
        //     (mirrors ProviderVersionSuperseded);
        // (b) ClaimVersionReversed (NEW) for the
        //     accumulator-reversal-intent signal.
        // Both go to the same Mongo append-only stream; both are
        // idempotent on EventId.
        var newVersionDomain = newVersion.ToClaim();
        try
        {
            await _versionEventPublisher.PublishVersionSupersededAsync(
                supersededPredecessor, newVersionDomain, request.AdjustmentReason, actorId, correlationId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "ClaimVersionSuperseded emission failed for predecessor {ClaimId}; supersession persisted, audit chain has gap",
                Sanitize(predecessor.Id));
        }

        try
        {
            await _versionEventPublisher.PublishVersionReversedAsync(
                supersededPredecessor, newVersion.Id, request.AdjustmentReason, actorId, correlationId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "ClaimVersionReversed emission failed for predecessor {ClaimId}; supersession persisted, audit chain has gap",
                Sanitize(predecessor.Id));
        }

        // Step 10 — emit ClaimVersionReversedMessage to the Service Bus
        // topic. 5.12b's ReversalRunService subscribes to this signal in
        // addition to listing PendingReversal adjustments via HTTP.
        try
        {
            var sbMessage = new ClaimVersionReversedMessage
            {
                TenantId = tenantId,
                ClaimId = predecessor.Id,
                ClaimVersionId = supersededPredecessor.ClaimVersionId,
                PredecessorVersionId = predecessor.Id,
                SupersessorClaimId = newVersion.Id,
                AdjustmentReason = request.AdjustmentReason,
                ActorId = actorId,
                CorrelationId = correlationId,
            };
            var sendOptions = new SendOptions(
                MessageId: $"reversed:{predecessor.Id}->{newVersion.Id}",
                CorrelationId: correlationId,
                Properties: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [ClaimVersionEventTopics.MessageTypeProperty] = ClaimVersionMessageTypes.Reversed,
                });

            await _messageBus.SendAsync(
                ClaimVersionEventTopics.TopicName, sbMessage, sendOptions, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "ClaimVersionReversed Service Bus emission failed for predecessor {ClaimId}; supersession persisted, ReversalRun signal missed",
                Sanitize(predecessor.Id));
        }

        // Step 11 — finalize the placeholder ClaimAdjustment row with
        // the NewClaimId now that submission + supersession have
        // succeeded. The placeholder was inserted in step 5; this is
        // the post-success update. Status remains
        // AwaitingReadjudication (Decision 18) — the new version's
        // pipeline is running asynchronously via Service Bus;
        // transition to PendingReversal is owned by the
        // orchestrator-finalize callback path (5.12b).
        adjustment.NewClaimId = newVersion.Id;
        await _adjustmentRepository.UpdateAsync(adjustment, ct);

        _logger.LogInformation(
            "Adjustment {AdjustmentId} created: predecessor {PredecessorClaimId} (chain {ChainKey} v{PredecessorVersion}) → " +
            "new version {NewClaimId} v{NewVersion}; reason='{Reason}'",
            Sanitize(adjustment.Id),
            Sanitize(predecessor.Id),
            Sanitize(chainKey),
            predecessor.VersionNumber,
            Sanitize(newVersion.Id),
            newVersion.VersionNumber,
            Sanitize(request.AdjustmentReason));

        return ClaimAdjustmentResult.Created(adjustment, newVersion);
    }

    public async Task OnNewVersionFinalizedAsync(
        string tenantId,
        string newClaimId,
        ClaimAdjudicationOutcome outcome,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tenantId)) return;
        if (string.IsNullOrWhiteSpace(newClaimId)) return;

        // Pend leaves the new version in human-review limbo; the adjustment
        // stays AwaitingReadjudication until the version reaches a real
        // terminal state (Approved/Paid/Denied) via the operator
        // resolution path. No-op here.
        if (outcome == ClaimAdjudicationOutcome.Pend) return;

        var adjustment = await _adjustmentRepository
            .GetByNewClaimIdAsync(tenantId, newClaimId, ct);
        if (adjustment == null)
        {
            // Most fresh submissions hit this path (no in-flight adjustment
            // for the new version). Logged at debug to keep the orchestrator
            // path quiet on the steady state.
            _logger.LogDebug(
                "OnNewVersionFinalizedAsync: no adjustment found for new claim {NewClaimId}; orchestrator no-op",
                Sanitize(newClaimId));
            return;
        }

        // Idempotency — re-invocation is harmless. The orchestrator's
        // event emission is at-least-once on the Service Bus contract;
        // this callback is in-process so duplicates are rare but possible
        // when the orchestrator retries after a transient downstream failure.
        if (adjustment.Status != ClaimAdjustmentStatus.AwaitingReadjudication)
        {
            _logger.LogInformation(
                "OnNewVersionFinalizedAsync: adjustment {AdjustmentId} already in {Status}; idempotent no-op",
                Sanitize(adjustment.Id), adjustment.Status);
            return;
        }

        var nextStatus = outcome switch
        {
            // Pass and Deny are both terminal pipeline outcomes —
            // predecessor's accumulator impact + provider payment still
            // need unwinding via 5.12b ReversalRun, regardless of whether
            // the corrected version was Approved or Denied.
            ClaimAdjudicationOutcome.Pass => ClaimAdjustmentStatus.PendingReversal,
            ClaimAdjudicationOutcome.Deny => ClaimAdjustmentStatus.PendingReversal,
            // Reject is pre-adjudication (scrubbing failure on the new
            // version). Operator must triage via the supersession-rollback
            // path; the adjustment goes Failed.
            ClaimAdjudicationOutcome.Reject => ClaimAdjustmentStatus.Failed,
            _ => adjustment.Status, // already filtered Pend above
        };

        adjustment.Status = nextStatus;
        adjustment.ReadjudicationCompletedAt = DateTime.UtcNow;
        if (nextStatus == ClaimAdjustmentStatus.Failed)
        {
            adjustment.FailureReason =
                $"Re-adjudication for new version {newClaimId} resulted in pipeline outcome {outcome}";
        }

        await _adjustmentRepository.UpdateAsync(adjustment, ct);

        _logger.LogInformation(
            "Adjustment {AdjustmentId} transitioned AwaitingReadjudication → {Status} on outcome {Outcome} for new version {NewClaimId}",
            Sanitize(adjustment.Id), nextStatus, outcome, Sanitize(newClaimId));
    }

    public async Task MarkActiveOnReversalAsync(
        string tenantId,
        string predecessorClaimId,
        string reversalRunId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tenantId)) return;
        if (string.IsNullOrWhiteSpace(predecessorClaimId)) return;
        if (string.IsNullOrWhiteSpace(reversalRunId)) return;

        var adjustment = await _adjustmentRepository.GetByPredecessorAndStatusAsync(
            tenantId, predecessorClaimId, ClaimAdjustmentStatus.PendingReversal, ct);
        if (adjustment == null)
        {
            // Operator-initiated void without ReversalRun, or void of a
            // claim that wasn't part of an adjustment chain. Either way
            // the adjustment lifecycle is not what's driving this void.
            _logger.LogDebug(
                "MarkActiveOnReversalAsync: no PendingReversal adjustment for predecessor {ClaimId}; void completes without lifecycle transition",
                Sanitize(predecessorClaimId));
            return;
        }

        adjustment.Status = ClaimAdjustmentStatus.Active;
        adjustment.ReversalRunId = reversalRunId;
        adjustment.ReversalCompletedAt = DateTime.UtcNow;
        await _adjustmentRepository.UpdateAsync(adjustment, ct);

        _logger.LogInformation(
            "Adjustment {AdjustmentId} transitioned PendingReversal → Active on ReversalRun {ReversalRunId} for predecessor {ClaimId}",
            Sanitize(adjustment.Id), Sanitize(reversalRunId), Sanitize(predecessorClaimId));
    }

    private async Task TryReleaseChainLockAsync(ClaimAdjustment placeholder, CancellationToken ct)
    {
        try
        {
            var deleted = await _adjustmentRepository.DeleteAsync(placeholder.TenantId, placeholder.Id, ct);
            if (!deleted)
            {
                _logger.LogWarning(
                    "Chain-lock release for adjustment {AdjustmentId} on chain {ClaimVersionId} matched 0 rows; chain may stay locked",
                    Sanitize(placeholder.Id), Sanitize(placeholder.ClaimVersionId));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to release chain lock for adjustment {AdjustmentId} on chain {ClaimVersionId}; chain may stay locked",
                Sanitize(placeholder.Id), Sanitize(placeholder.ClaimVersionId));
        }
    }

    internal static string ComputeRequestHash(string predecessorClaimId, ClaimAdjustmentRequest request)
    {
        // Stable hash of the request body — used to detect "same key,
        // different body" idempotency violations. JSON serialization
        // gives a deterministic-enough surface for the body shapes we
        // accept (operator UI submits the same payload on retry); we
        // include the predecessor id so two different chains with the
        // same idempotency key (operator pasted the wrong key) are
        // treated as distinct rather than colliding.
        var canonical = new
        {
            PredecessorClaimId = predecessorClaimId,
            request.AdjustmentReason,
            request.Notes,
            request.CorrectedClaim,
        };
        var json = JsonSerializer.Serialize(canonical, new JsonSerializerOptions
        {
            WriteIndented = false,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        });
        var bytes = Encoding.UTF8.GetBytes(json);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }

    private static string Sanitize(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", string.Empty).Replace("\n", string.Empty);
}
