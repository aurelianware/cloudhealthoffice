using Microsoft.Extensions.Options;
using PaymentService.Models;
using PaymentService.Repositories;

namespace PaymentService.Services;

/// <summary>The run, or its reservation of the claim, does not exist in the caller's tenant. Controllers answer 404.</summary>
public sealed class ReservationNotFoundException : Exception
{
    public ReservationNotFoundException(string message) : base(message) { }
}

/// <summary>The reservation may not be released now (the claim is paid, the run is still executing, or it changed). Controllers answer 409.</summary>
public sealed class ReservationConflictException : Exception
{
    public ReservationConflictException(string message) : base(message) { }
}

/// <summary>
/// Reconciles claim reservations left reserved but unpaid. A run reserves a
/// claim before paying (or reversing) it; a run that fails after trying to pay
/// keeps the reservation, because the payment may exist. Left alone, such a
/// claim is never paid by any later run.
///
/// <para><b>Automatic release (safe cases only).</b> The hosted job
/// (<see cref="ReservationReconciliationJob"/>, as payment-service, per tenant,
/// no claims-service calls) releases a reservation when its run is Failed or
/// Cancelled, the reservation and the run's end are older than
/// <c>PaymentRuns:ReservationGracePeriod</c> (default 30 minutes), and
/// payment-service holds no payment record and no 835 envelope for the claim in
/// the tenant. The delete is conditional on the reservation being the one
/// classified, the release is audited, and the claim is listed in the run's
/// <c>ReleasedReservationClaimIds</c>.</para>
///
/// <para><b>Everything else needs a person.</b> A reservation whose claim has a
/// payment or an 835, whose run is still Running past
/// <c>PaymentRuns:ReservationStuckThreshold</c> (default 2 hours), or that the
/// job cannot classify, is flagged NeedsAttention with a reason and listed in
/// the run's <c>ReservationsNeedingAttention</c>. A user with payments:approve
/// who did not execute the run releases it with a reason
/// (<see cref="ReleaseManuallyAsync"/>); a claim with a Posted or
/// PaidPendingFinalize payment is not unpaid and is refused.</para>
/// </summary>
public interface IReservationReconciliationService
{
    /// <summary>One pass over the current tenant's reservations (tenant from the request context).</summary>
    Task<ReconciliationPassResult> ReconcileCurrentTenantAsync(CancellationToken cancellationToken = default);

    /// <summary>A second approver releases one reservation of a run in the caller's tenant.</summary>
    Task<ReservationReleaseResult> ReleaseManuallyAsync(ClaimReservationKind kind, string runId, string claimId, string? reason);

    /// <summary>The caller's tenant's reservations flagged NeedsAttention.</summary>
    Task<IReadOnlyList<ReservationNeedingAttentionView>> ListNeedingAttentionAsync();
}

public sealed class ReservationReconciliationService : IReservationReconciliationService
{
    public const string AutoReleaseReason = "auto-release: run failed before payment";
    public const string JobActor = "payment-service";

    public static readonly EventId AutoReleasedEvent = new(4641, "ClaimReservationAutoReleased");
    public static readonly EventId FlaggedEvent = new(4642, "ClaimReservationNeedsAttention");
    public static readonly EventId ReleasedEvent = new(4643, "ClaimReservationReleased");

    private readonly IClaimReservationRepository _reservations;
    private readonly IPaymentRunRepository _paymentRuns;
    private readonly IReversalRunRepository _reversalRuns;
    private readonly IPaymentRepository _payments;
    private readonly IEraEnvelopeRepository _envelopes;
    private readonly IReservationAuditLog _audit;
    private readonly IRunSeparationOfDuties _separationOfDuties;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly TimeProvider _time;
    private readonly IOptions<ReservationReconciliationOptions> _options;
    private readonly ILogger<ReservationReconciliationService> _logger;

    public ReservationReconciliationService(
        IClaimReservationRepository reservations,
        IPaymentRunRepository paymentRuns,
        IReversalRunRepository reversalRuns,
        IPaymentRepository payments,
        IEraEnvelopeRepository envelopes,
        IReservationAuditLog audit,
        IRunSeparationOfDuties separationOfDuties,
        IHttpContextAccessor httpContextAccessor,
        TimeProvider time,
        IOptions<ReservationReconciliationOptions> options,
        ILogger<ReservationReconciliationService> logger)
    {
        _reservations = reservations;
        _paymentRuns = paymentRuns;
        _reversalRuns = reversalRuns;
        _payments = payments;
        _envelopes = envelopes;
        _audit = audit;
        _separationOfDuties = separationOfDuties;
        _httpContextAccessor = httpContextAccessor;
        _time = time;
        _options = options;
        _logger = logger;
    }

    // ── automatic pass ────────────────────────────────────────────────

    public async Task<ReconciliationPassResult> ReconcileCurrentTenantAsync(CancellationToken cancellationToken = default)
    {
        var tenantId = CurrentTenant();
        var options = _options.Value;
        var now = Now();
        var result = new ReconciliationPassResult { TenantId = tenantId };

        var all = await _reservations.ListByTenantAsync(tenantId);
        result.Examined = all.Count;

        foreach (var kindGroup in all.Where(r => r.TenantId == tenantId).GroupBy(r => r.Kind))
        {
            var kind = kindGroup.Key;
            var reversal = kind == ClaimReservationKind.Reversal;
            var claimIds = kindGroup.Select(r => r.ClaimId).Distinct(StringComparer.Ordinal).ToList();
            var paid = new HashSet<string>(
                await _payments.GetClaimIdsWithPaymentAsync(claimIds, reversal) ?? Array.Empty<string>(), StringComparer.Ordinal);
            var enveloped = new HashSet<string>(
                await _envelopes.GetClaimIdsWithEnvelopeAsync(claimIds, reversal) ?? Array.Empty<string>(), StringComparer.Ordinal);

            foreach (var runGroup in kindGroup.GroupBy(r => r.RunId, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var run = await LoadRunAsync(kind, runGroup.Key);
                var outcomes = new ReservationOutcomes();

                foreach (var reservation in runGroup)
                {
                    var hasPayment = paid.Contains(reservation.ClaimId);
                    var hasEnvelope = enveloped.Contains(reservation.ClaimId);
                    var verdict = Classify(reservation, run, hasPayment, hasEnvelope, now, options);
                    await ActAsync(reservation, run, verdict, hasPayment, hasEnvelope, now, outcomes, result);
                }

                if (run != null && !outcomes.IsEmpty)
                    await RecordOutcomesAsync(kind, run.Id, outcomes);
            }
        }

        if (result.AutoReleased.Count + result.Flagged.Count + result.Cleared.Count > 0)
        {
            _logger.LogInformation(
                "Reservation reconciliation for tenant {TenantId}: {Examined} examined, {Released} auto-released, {Flagged} flagged, {Cleared} cleared, {Skipped} changed meanwhile",
                Sanitize(tenantId), result.Examined, result.AutoReleased.Count, result.Flagged.Count,
                result.Cleared.Count, result.SkippedChanged.Count);
        }
        return result;
    }

    private enum VerdictKind { Leave, Normal, AutoRelease, NeedsAttention }

    private sealed record Verdict(VerdictKind Kind, string? Reason = null);

    /// <summary>
    /// The only automatic release: run Failed or Cancelled, no payment, no 835,
    /// past the grace period. Everything that is not plainly fine is flagged.
    /// </summary>
    private static Verdict Classify(
        ClaimReservation reservation, RunView? run, bool hasPayment, bool hasEnvelope,
        DateTime now, ReservationReconciliationOptions options)
    {
        if (run == null)
            return new Verdict(VerdictKind.NeedsAttention,
                $"run {reservation.RunNumber ?? reservation.RunId} holding the reservation was not found in this tenant");

        switch (run.State)
        {
            case RunState.Running:
            {
                var startedAt = run.StartedAt ?? reservation.ReservedAt;
                return now - startedAt >= options.ReservationStuckThreshold
                    ? new Verdict(VerdictKind.NeedsAttention,
                        $"run is still Running beyond the stuck threshold ({options.ReservationStuckThreshold}); started {startedAt:O}")
                    : new Verdict(VerdictKind.Leave);
            }

            case RunState.Pending:
                return new Verdict(VerdictKind.NeedsAttention,
                    "run is Pending but holds a reservation; runs reserve only while executing");

            case RunState.Completed:
                return hasPayment
                    ? new Verdict(VerdictKind.Normal)
                    : new Verdict(VerdictKind.NeedsAttention,
                        "run completed but payment-service holds no payment for the claim" +
                        (hasEnvelope ? " (an 835 lists it)" : string.Empty));

            case RunState.Failed:
            case RunState.Cancelled:
            {
                if (hasPayment || hasEnvelope)
                {
                    var what = hasPayment && hasEnvelope ? "a payment record and an 835"
                        : hasPayment ? "a payment record" : "an 835 envelope";
                    return new Verdict(VerdictKind.NeedsAttention,
                        $"run {run.State} but {what} exists for the claim; it may have been paid");
                }

                var since = run.EndedAt is { } ended && ended > reservation.ReservedAt ? ended : reservation.ReservedAt;
                return now - since >= options.ReservationGracePeriod
                    ? new Verdict(VerdictKind.AutoRelease, AutoReleaseReason)
                    : new Verdict(VerdictKind.Leave);
            }

            default:
                return new Verdict(VerdictKind.NeedsAttention, $"run status {run.Status} cannot be classified");
        }
    }

    private async Task ActAsync(
        ClaimReservation reservation, RunView? run, Verdict verdict, bool hasPayment, bool hasEnvelope,
        DateTime now, ReservationOutcomes outcomes, ReconciliationPassResult result)
    {
        switch (verdict.Kind)
        {
            case VerdictKind.AutoRelease:
            {
                // Conditional: deletes only the reservation classified above. If a
                // person released it and a new run reserved the claim meanwhile,
                // the new run's reservation stays.
                if (!await _reservations.TryDeleteIfUnchangedAsync(reservation))
                {
                    result.SkippedChanged.Add(reservation.ClaimId);
                    _logger.LogInformation(
                        "Reservation of claim {ClaimId} changed before auto-release; left as it is", Sanitize(reservation.ClaimId));
                    return;
                }

                await _audit.RecordAsync(Entry(reservation, run, ReservationAuditAction.AutoReleased, AutoReleaseReason,
                    JobActor, actorIsService: true, hasPayment, hasEnvelope, now));
                outcomes.Released.Add(reservation.ClaimId);
                outcomes.Warnings.Add(
                    $"Reservation of claim {reservation.ClaimId} released by {JobActor} at {now:O}: {AutoReleaseReason}; a later run may pay it");
                result.AutoReleased.Add(reservation.ClaimId);
                _logger.LogWarning(AutoReleasedEvent,
                    "Auto-released reservation of claim {ClaimId} held by {Kind} run {RunNumber} ({RunStatus}) in tenant {TenantId}",
                    Sanitize(reservation.ClaimId), reservation.Kind, Sanitize(run?.Number), run?.Status, Sanitize(reservation.TenantId));
                return;
            }

            case VerdictKind.NeedsAttention:
            {
                if (reservation.NeedsAttention && reservation.AttentionReason == verdict.Reason)
                    return; // flagged already, for the same reason

                if (!await _reservations.TrySetAttentionIfUnchangedAsync(reservation, verdict.Reason, now))
                {
                    result.SkippedChanged.Add(reservation.ClaimId);
                    return;
                }

                await _audit.RecordAsync(Entry(reservation, run, ReservationAuditAction.FlaggedNeedsAttention, verdict.Reason!,
                    JobActor, actorIsService: true, hasPayment, hasEnvelope, now));
                outcomes.Attention.Add(new ReservationAttention { ClaimId = reservation.ClaimId, Reason = verdict.Reason!, FlaggedAt = now });
                outcomes.Warnings.Add($"Reservation of claim {reservation.ClaimId} needs attention: {verdict.Reason}");
                result.Flagged.Add(reservation.ClaimId);
                _logger.LogWarning(FlaggedEvent,
                    "Reservation of claim {ClaimId} held by {Kind} run {RunNumber} in tenant {TenantId} needs attention: {Reason}",
                    Sanitize(reservation.ClaimId), reservation.Kind, Sanitize(run?.Number ?? reservation.RunId),
                    Sanitize(reservation.TenantId), Sanitize(verdict.Reason));
                return;
            }

            case VerdictKind.Normal when reservation.NeedsAttention:
            {
                // The run finished and paid the claim: an ordinary paid reservation.
                if (await _reservations.TrySetAttentionIfUnchangedAsync(reservation, null, null))
                {
                    outcomes.Cleared.Add(reservation.ClaimId);
                    result.Cleared.Add(reservation.ClaimId);
                }
                return;
            }

            default:
                return;
        }
    }

    // ── manual release ────────────────────────────────────────────────

    public async Task<ReservationReleaseResult> ReleaseManuallyAsync(
        ClaimReservationKind kind, string runId, string claimId, string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("A reason is required to release a claim reservation.", nameof(reason));
        reason = reason.Trim();

        var run = await LoadRunAsync(kind, runId)
            ?? throw new ReservationNotFoundException($"{RunKindName(kind)} {runId} not found");

        // A user with payments:approve, never a service token, never the executor.
        var releasedBy = _separationOfDuties.EnsureMayReleaseReservation(RunKindName(kind), run.Number, run.ExecutedBy);

        var reservation = await _reservations.GetAsync(kind, run.TenantId, claimId);
        if (reservation == null || reservation.RunId != run.Id)
            throw new ReservationNotFoundException(
                $"{RunKindName(kind)} {run.Number} holds no {kind.ToString().ToLowerInvariant()} reservation for claim {claimId}");

        var now = Now();
        if (run.State == RunState.Pending
            || (run.State == RunState.Running
                && now - (run.StartedAt ?? reservation.ReservedAt) < _options.Value.ReservationStuckThreshold))
        {
            throw new ReservationConflictException(
                $"{RunKindName(kind)} {run.Number} is still executing; its reservations can be released once it has " +
                $"finished, or once it has been Running longer than {_options.Value.ReservationStuckThreshold}.");
        }

        var reversal = kind == ClaimReservationKind.Reversal;
        var payments = (await _payments.GetByClaimIdAsync(claimId) ?? Enumerable.Empty<Payment>())
            .Where(p => p.IsReversal == reversal)
            .ToList();
        var paid = payments.FirstOrDefault(p => p.Status is PaymentStatus.Posted or PaymentStatus.PaidPendingFinalize);
        if (paid != null)
        {
            var retry = reversal
                ? $"POST /api/reversalruns/{paid.RunId ?? run.Id}/void"
                : $"POST /api/paymentruns/{paid.RunId ?? run.Id}/finalize";
            throw new ReservationConflictException(
                $"Claim {claimId} is not unpaid: {(reversal ? "reversal " : string.Empty)}payment {paid.CheckNumber} is {paid.Status}. " +
                $"The reservation stays; if claims-service has not recorded it, retry with {retry}.");
        }

        var hasEnvelope = (await _envelopes.GetClaimIdsWithEnvelopeAsync(new[] { claimId }, reversal) ?? Array.Empty<string>())
            .Contains(claimId);

        if (!await _reservations.TryDeleteIfUnchangedAsync(reservation))
            throw new ReservationConflictException(
                $"The reservation of claim {claimId} changed while it was being released; reload and try again.");

        await _audit.RecordAsync(Entry(reservation, run, ReservationAuditAction.Released, reason,
            releasedBy, actorIsService: false, payments.Count > 0, hasEnvelope, now));

        var outcomes = new ReservationOutcomes();
        outcomes.Released.Add(claimId);
        outcomes.Warnings.Add($"Reservation of claim {claimId} released by {releasedBy} at {now:O}: {reason}");
        await RecordOutcomesAsync(kind, run.Id, outcomes);

        _logger.LogWarning(ReleasedEvent,
            "Reservation of claim {ClaimId} held by {Kind} run {RunNumber} released by {User} in tenant {TenantId}",
            Sanitize(claimId), kind, Sanitize(run.Number), Sanitize(releasedBy), Sanitize(run.TenantId));

        return new ReservationReleaseResult
        {
            Kind = kind.ToString(),
            RunId = run.Id,
            RunNumber = run.Number,
            ClaimId = claimId,
            ReleasedBy = releasedBy,
            Reason = reason,
            ReleasedAt = now,
            EnvelopeFound = hasEnvelope,
        };
    }

    public async Task<IReadOnlyList<ReservationNeedingAttentionView>> ListNeedingAttentionAsync()
    {
        var tenantId = CurrentTenant();
        var flagged = await _reservations.ListByTenantAsync(tenantId, needsAttentionOnly: true);
        return flagged
            .Where(r => r.TenantId == tenantId && r.NeedsAttention)
            .Select(r => new ReservationNeedingAttentionView
            {
                Kind = r.Kind.ToString(),
                ClaimId = r.ClaimId,
                RunId = r.RunId,
                RunNumber = r.RunNumber,
                Reason = r.AttentionReason,
                FlaggedAt = r.FlaggedAt,
                ReservedAt = r.ReservedAt,
                ReservedBy = r.ReservedBy,
                ReleasePath = $"/api/{(r.Kind == ClaimReservationKind.Reversal ? "reversalruns" : "paymentruns")}/" +
                              $"{Uri.EscapeDataString(r.RunId)}/reservations/{Uri.EscapeDataString(r.ClaimId)}/release",
            })
            .ToList();
    }

    // ── helpers ───────────────────────────────────────────────────────

    private enum RunState { Pending, Running, Completed, Failed, Cancelled }

    /// <summary>The fields reconciliation needs from a payment run or a reversal run.</summary>
    private sealed record RunView(
        string Id, string Number, string TenantId, RunState State, string Status,
        string? ExecutedBy, DateTime? StartedAt, DateTime? EndedAt);

    private async Task<RunView?> LoadRunAsync(ClaimReservationKind kind, string runId)
    {
        if (kind == ClaimReservationKind.Reversal)
        {
            var r = await _reversalRuns.GetByIdAsync(runId);
            return r == null ? null : new RunView(r.Id, r.ReversalRunNumber, r.TenantId, (RunState)(int)r.Status,
                r.Status.ToString(), r.ExecutedBy, r.ExecutionStartedAt, Latest(r.ExecutionCompletedAt, r.CancelledAt));
        }

        var p = await _paymentRuns.GetByIdAsync(runId);
        return p == null ? null : new RunView(p.Id, p.PaymentRunNumber, p.TenantId, (RunState)(int)p.Status,
            p.Status.ToString(), p.ExecutedBy, p.ExecutionStartedAt, Latest(p.ExecutionCompletedAt, p.CancelledAt));
    }

    private Task<bool> RecordOutcomesAsync(ClaimReservationKind kind, string runId, ReservationOutcomes outcomes)
        => kind == ClaimReservationKind.Reversal
            ? _reversalRuns.RecordReservationOutcomesAsync(runId, outcomes)
            : _paymentRuns.RecordReservationOutcomesAsync(runId, outcomes);

    private static ReservationAuditEntry Entry(
        ClaimReservation reservation, RunView? run, ReservationAuditAction action, string reason,
        string actor, bool actorIsService, bool paymentFound, bool envelopeFound, DateTime at)
        => new()
        {
            TenantId = reservation.TenantId,
            Kind = reservation.Kind.ToString(),
            RunId = reservation.RunId,
            RunNumber = run?.Number ?? reservation.RunNumber,
            ClaimId = reservation.ClaimId,
            Action = action,
            Reason = reason,
            Actor = actor,
            ActorIsService = actorIsService,
            At = at,
            RunStatus = run?.Status,
            RunExecutedBy = run?.ExecutedBy,
            ReservedAt = reservation.ReservedAt,
            ReservedBy = reservation.ReservedBy,
            PaymentFound = paymentFound,
            EnvelopeFound = envelopeFound,
        };

    private static DateTime? Latest(DateTime? a, DateTime? b)
        => a == null ? b : b == null ? a : (a > b ? a : b);

    private static string RunKindName(ClaimReservationKind kind)
        => kind == ClaimReservationKind.Reversal ? "reversal run" : "payment run";

    private DateTime Now() => _time.GetUtcNow().UtcDateTime;

    private string CurrentTenant()
    {
        var tenantId = _httpContextAccessor.HttpContext?.Items["TenantId"]?.ToString();
        if (string.IsNullOrEmpty(tenantId))
            throw new InvalidOperationException("TenantId not found in request context");
        return tenantId;
    }

    private static string Sanitize(string? value)
        => string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", string.Empty).Replace("\n", string.Empty);
}
