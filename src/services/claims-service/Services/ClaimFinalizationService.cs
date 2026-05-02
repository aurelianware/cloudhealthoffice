using ClaimsService.Models;
using ClaimsService.Repositories;

namespace ClaimsService.Services;

/// <summary>
/// Owns the Adjudicated → Paid transition for a claim version. The
/// canonical 5.10 finalize path: payment-service issues a
/// <c>POST /api/claims/{id}/remittance</c> per claim during PaymentRun
/// execution; the controller delegates here so the lifecycle write is
/// idempotent, source-state-validated, and emits the
/// <c>ClaimVersionPaid</c> event into the Mongo version chain alongside
/// the existing Kafka <c>claims.finalized.v1</c> notification.
///
/// Phase 1 only supports the Paid transition (status==Paid). Other
/// state transitions (Voided, Reversed) are deferred to capability 5.12
/// (Adjustment Workflow). A request whose source claim is not in the
/// terminal-Paid-eligible set (Approved or PartiallyPaid) returns
/// <see cref="ClaimFinalizationOutcome.InvalidSourceState"/>.
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

public class ClaimFinalizationService : IClaimFinalizationService
{
    private readonly IClaimRepository _claimRepository;
    private readonly IClaimVersionEventPublisher _versionEventPublisher;
    private readonly IClaimEventPublisher _kafkaEventPublisher;
    private readonly ILogger<ClaimFinalizationService> _logger;

    public ClaimFinalizationService(
        IClaimRepository claimRepository,
        IClaimVersionEventPublisher versionEventPublisher,
        IClaimEventPublisher kafkaEventPublisher,
        ILogger<ClaimFinalizationService> logger)
    {
        _claimRepository = claimRepository;
        _versionEventPublisher = versionEventPublisher;
        _kafkaEventPublisher = kafkaEventPublisher;
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

    private static string Sanitize(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", string.Empty).Replace("\n", string.Empty);
}
