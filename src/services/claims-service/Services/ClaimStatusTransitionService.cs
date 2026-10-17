using ClaimsService.Models;
using ClaimsService.Repositories;

namespace ClaimsService.Services;

/// <summary>
/// The operational (non-outcome) status transitions behind
/// <c>PUT /api/claims/{id}/status</c>: 277-style intake statuses
/// (<see cref="ClaimStatus.Received"/>, <see cref="ClaimStatus.InAdjudication"/>)
/// and <see cref="ClaimStatus.Voided"/>, which is the void transition
/// (<see cref="IClaimVoidService"/>). Outcome statuses are refused before
/// this runs (<see cref="ClaimStatusTransitionService.OutcomeStatusRefusal"/>);
/// the controller checks <c>claims:void</c> for a void.
/// </summary>
public interface IClaimStatusTransitionService
{
    /// <summary>
    /// Applies the transition. <paramref name="getTenantId"/> is read only
    /// when the transition needs the tenant, so a missing tenant fails at the
    /// same point it always did.
    /// </summary>
    Task<ClaimStatusTransitionResult> TransitionAsync(
        string id,
        ClaimStatusUpdate statusUpdate,
        Func<string> getTenantId,
        string? correlationId,
        CancellationToken ct);
}

/// <summary>The outcome of <see cref="IClaimStatusTransitionService.TransitionAsync"/>.</summary>
public abstract record ClaimStatusTransitionResult
{
    private ClaimStatusTransitionResult() { }

    /// <summary>The claim as persisted (its <c>Status</c> is what actually persisted).</summary>
    public sealed record Updated(Claim Claim) : ClaimStatusTransitionResult;

    /// <summary>No such claim (404 with <paramref name="Message"/>).</summary>
    public sealed record ClaimNotFound(string Message) : ClaimStatusTransitionResult;

    /// <summary>The request was a void; this is the void transition's result.</summary>
    public sealed record Voided(ClaimVoidResult VoidResult) : ClaimStatusTransitionResult;
}

/// <summary>Default <see cref="IClaimStatusTransitionService"/> (moved from <c>ClaimsController.UpdateClaimStatus</c>).</summary>
public sealed class ClaimStatusTransitionService : IClaimStatusTransitionService
{
    private readonly IClaimRepository _claimRepository;
    private readonly IClaimEventPublisher _eventPublisher;
    private readonly IClaimVoidService _voidService;
    private readonly ILogger _logger;

    public ClaimStatusTransitionService(
        IClaimRepository claimRepository,
        IClaimEventPublisher eventPublisher,
        IClaimVoidService voidService,
        ILogger<ClaimStatusTransitionService> logger)
        : this(claimRepository, eventPublisher, voidService, (ILogger)logger)
    {
    }

    /// <summary>
    /// For <c>ClaimsController</c>'s direct-construction constructor, which
    /// passes its own logger so the log output is unchanged.
    /// </summary>
    internal ClaimStatusTransitionService(
        IClaimRepository claimRepository,
        IClaimEventPublisher eventPublisher,
        IClaimVoidService voidService,
        ILogger logger)
    {
        _claimRepository = claimRepository;
        _eventPublisher = eventPublisher;
        _voidService = voidService;
        _logger = logger;
    }

    /// <summary>
    /// The 422 body for a status only the adjudication pipeline or a
    /// dedicated post-adjudication action may set; null for an operational
    /// status <c>PUT {id}/status</c> accepts.
    /// </summary>
    public static object? OutcomeStatusRefusal(ClaimStatus status)
    {
        var path = status switch
        {
            ClaimStatus.Approved or ClaimStatus.Deny =>
                "Adjudicated outcomes (Pay / Deny) are set only by the adjudication pipeline: submit the claim " +
                "(POST /api/v1/claims) and let it adjudicate, or resolve a pended, Pay or Deny claim through " +
                "POST /api/claims/work-queue/{id}/resolve or /override, which re-run the pipeline.",
            ClaimStatus.Pended =>
                "Pends are set only by the adjudication pipeline. To act on a pended claim use " +
                "POST /api/claims/work-queue/{id}/resolve or /override.",
            ClaimStatus.Paid or ClaimStatus.PartiallyPaid or ClaimStatus.Denied =>
                "Finalized statuses (Paid / Denied) are set by the payment cycle's remittance: " +
                "POST /api/claims/{id}/remittance (or /inbound-remittance for a payer 835).",
            _ => null,
        };

        return path is null
            ? null
            : new
            {
                error = $"Status {status} cannot be set through PUT /api/claims/{{id}}/status.",
                detail = path + " To void a claim use POST /api/claims/{id}/void.",
            };
    }

    /// <inheritdoc />
    public async Task<ClaimStatusTransitionResult> TransitionAsync(
        string id,
        ClaimStatusUpdate statusUpdate,
        Func<string> getTenantId,
        string? correlationId,
        CancellationToken ct)
    {
        _logger.LogInformation(
            "Updating claim {Id} status to {Status}",
            SanitizeForLog(id), statusUpdate.Status);

        // A void is the void transition (VoidAsync), never a bare status
        // write: it saves the terminal accumulator reversal with the void and
        // emits ClaimVersionVoided, exactly as POST {id}/void does.
        if (statusUpdate.Status == ClaimStatus.Voided)
        {
            var voidResult = await _voidService.VoidWithReasonAsync(
                id,
                string.IsNullOrWhiteSpace(statusUpdate.Notes) ? "Voided via PUT /api/claims/{id}/status" : statusUpdate.Notes,
                getTenantId,
                correlationId,
                ct);
            return new ClaimStatusTransitionResult.Voided(voidResult);
        }

        var claim = await _claimRepository.GetByIdAsync(id);
        if (claim == null)
        {
            return new ClaimStatusTransitionResult.ClaimNotFound($"Claim {id} not found");
        }

        var statusResult = await _claimRepository.TryTransitionStatusAsync(getTenantId(), id, statusUpdate.Status, ct);
        if (statusResult.Outcome == StatusWriteOutcome.NotFound)
        {
            return new ClaimStatusTransitionResult.ClaimNotFound($"Claim {id} not found");
        }

        claim.Status = statusResult.PersistedStatus!.Value;
        claim.LastUpdatedDate = DateTime.UtcNow;

        if (statusResult.Outcome == StatusWriteOutcome.Suppressed)
        {
            _logger.LogInformation(
                "Status transition suppressed for claim {Id}: requested={Requested}, persisted={Persisted}",
                SanitizeForLog(id), statusUpdate.Status, statusResult.PersistedStatus);
        }
        else
        {
            if (statusUpdate.Status == ClaimStatus.Received)
            {
                // The payer receipt date is stamped once, at intake
                // (ClaimSubmissionService); a later status write must not
                // move the timely-filing / prompt-pay clock. Only a row that
                // predates intake stamping gets one here.
                claim.ReceivedDate ??= DateTime.UtcNow;
            }

            if (!string.IsNullOrEmpty(statusUpdate.Notes))
            {
                claim.ClaimNotes = string.IsNullOrEmpty(claim.ClaimNotes)
                    ? statusUpdate.Notes
                    : $"{claim.ClaimNotes}\n{DateTime.UtcNow:yyyy-MM-dd HH:mm}: {statusUpdate.Notes}";
            }
        }

        // The finalized event is stored with this write (outbox). The status
        // itself was written just above by TryTransitionStatusAsync.
        if (statusResult.Outcome == StatusWriteOutcome.Applied && IsTerminalStatus(claim.Status))
            claim.AddPendingClaimEvent(_eventPublisher.PrepareClaimFinalized(claim, getTenantId()));
        var updated = await _claimRepository.UpdateAsync(claim);

        if (statusResult.Outcome == StatusWriteOutcome.Applied && IsTerminalStatus(updated.Status))
        {
            await _eventPublisher.PublishClaimFinalizedAsync(updated, getTenantId());
        }

        return new ClaimStatusTransitionResult.Updated(updated);
    }

    /// <summary>
    /// A decided status (either phase) that publishes claims.finalized.v1:
    /// Pay / Deny (adjudicated) and Paid / Denied / Voided (finalized).
    /// </summary>
    private static bool IsTerminalStatus(ClaimStatus status) =>
        ClaimStatusSemantics.IsPayOutcome(status)
            || ClaimStatusSemantics.IsDenied(status)
            || status == ClaimStatus.Voided;

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
    }
}
