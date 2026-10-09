using AccumulatorService.Models;
using AccumulatorService.Repositories;
using CloudHealthOffice.Events;

namespace AccumulatorService.Services;

/// <summary>
/// Accumulator domain service. Owns two mutations:
///
///   1. <see cref="ApplyClaimFinalizedAsync"/> — idempotent, driven by Kafka.
///      Selects the snapshot by ServiceDate's plan year (NOT today's date) so a
///      retro-finalized claim from six months ago lands in the correct prior-year
///      bucket. If no snapshot covers the service date, emits an
///      OrphanAccumulatorClaim signal and skips — data-quality alert, not silent drop.
///
///   2. <see cref="AdjustAsync"/> — manual override by an authorized operator.
///      Every adjustment writes an AccumulatorEvent (audit) and publishes an
///      AccumulatorAdjustedEvent.
///
/// Snapshot mutations go through the event store: append the event first, then
/// project to the snapshot. Snapshot Version is bumped in lockstep with
/// AccumulatorEvent.Version so the two stay consistent under replay.
/// </summary>
public class AccumulatorService : IAccumulatorService
{
    private readonly IAccumulatorRepository _repo;
    private readonly IProcessedClaimStore _processed;
    private readonly IAccumulatorEventPublisher _publisher;
    private readonly ILogger<AccumulatorService> _logger;

    public AccumulatorService(
        IAccumulatorRepository repo,
        IProcessedClaimStore processed,
        IAccumulatorEventPublisher publisher,
        ILogger<AccumulatorService> logger)
    {
        _repo = repo;
        _processed = processed;
        _publisher = publisher;
        _logger = logger;
    }

    public async Task<AccumulatorResponse?> GetAsync(string tenantId, string memberId, DateTime? asOfDate, CancellationToken ct = default)
    {
        var target = asOfDate ?? DateTime.UtcNow.Date;
        var snapshot = await _repo.GetSnapshotByAsOfDateAsync(tenantId, memberId, target, ct);
        if (snapshot is null) return null;

        var recent = await _repo.GetEventsAsync(tenantId, memberId, take: 20, ct);
        return ToResponse(snapshot, recent);
    }

    public async Task<AccumulatorHistoryResponse> GetHistoryAsync(string tenantId, string memberId, CancellationToken ct = default)
    {
        var snapshots = await _repo.GetSnapshotsAsync(tenantId, memberId, ct);
        var events = await _repo.GetEventsAsync(tenantId, memberId, take: 200, ct);
        return new AccumulatorHistoryResponse
        {
            MemberId = memberId,
            Snapshots = snapshots.Select(s => new AccumulatorSnapshotSummary
            {
                PlanYearStart = s.PlanYearStart,
                PlanYearEnd = s.PlanYearEnd,
                IndividualDeductibleUsed = s.IndividualDeductibleUsed,
                IndividualDeductibleLimit = s.IndividualDeductibleLimit,
                IndividualOopUsed = s.IndividualOopUsed,
                IndividualOopLimit = s.IndividualOopLimit,
                Version = s.Version,
                LastUpdatedDate = s.LastUpdatedDate
            }).ToList(),
            Events = events.Select(ToActivity).ToList()
        };
    }

    /// <summary>
    /// How many times a write re-reads and retries after losing a version
    /// race (another writer took the next version first) before giving up;
    /// giving up throws so the consumer retries the message later.
    /// </summary>
    public const int MaxWriteAttempts = 8;

    public async Task<ApplyResult> ApplyClaimFinalizedAsync(ClaimFinalizedEvent evt, CancellationToken ct = default)
    {
        // A replacement (CLM05-3 = 7) or void (8) of an earlier claim: reverse
        // the original's deltas first, so the replacement does not count on
        // top of it. Idempotent with the original's own void event. Checked
        // before the claim's own status (PR #1278 re-review N5c): a
        // frequency-8 void claim arrives with FinalStatus "Reversed", and
        // treating it as a reversal of itself reversed nothing and left the
        // original counted.
        if (evt.ClaimFrequencyCode is "7" or "8"
            && !string.IsNullOrWhiteSpace(evt.OriginalClaimId)
            && !string.Equals(evt.OriginalClaimId, evt.ClaimId, StringComparison.Ordinal))
        {
            var reversed = await ReverseClaimAsync(evt.TenantId, evt.OriginalClaimId!, evt.ClaimId, ct);
            if (evt.ClaimFrequencyCode == "8" || reversed.Outcome == ApplyOutcome.InProgress) return reversed;
        }

        // A void (claims-service maps Voided → "Reversed"): back out what
        // the claim applied. Keyed separately from the apply, so the claim's
        // own (tenantId, claimId) marker — already "Applied" — does not
        // swallow it.
        if (IsReversal(evt.FinalStatus))
            return await ReverseClaimAsync(evt.TenantId, evt.ClaimId, evt.ClaimId, ct);

        // A denied claim contributes nothing (round 3, H4): a claim pended
        // after benefit calculation and then denied by an examiner can still
        // carry the amounts priced before the denial. If it had applied,
        // back that out.
        if (IsDenied(evt.FinalStatus))
            return await SkipDeniedAsync(evt, ct);

        // Two-phase idempotency — (tenantId, claimId) is the dedupe key even across
        // regenerated EventIds (re-finalization must not double-count). A Pending
        // marker from a crashed prior attempt (older than the lease) does NOT
        // block retry; a younger one is an attempt still in flight — retry later.
        var begin = await _processed.TryBeginAsync(evt.TenantId, evt.ClaimId, ct);
        if (begin == BeginClaimOutcome.AlreadyApplied)
        {
            // Also a claim reversed before its apply arrived (tombstone,
            // round 3 B3): a replacement already backed it out.
            var marker = await _processed.GetAsync(evt.TenantId, evt.ClaimId, ct);
            var reason = marker?.Outcome == ReversedBeforeApplyOutcome ? ReversedBeforeApplyOutcome : "DuplicateClaim";
            _logger.LogInformation(
                "ClaimFinalizedEvent for claim {ClaimId} tenant {TenantId} not applied: {Reason}",
                SanitizeForLog(evt.ClaimId), SanitizeForLog(evt.TenantId), reason);
            return new ApplyResult(ApplyOutcome.Duplicate, null, null, reason);
        }
        if (begin == BeginClaimOutcome.InProgress)
            return new ApplyResult(ApplyOutcome.InProgress, null, null, "ApplyInProgress");

        for (var attempt = 0; attempt < MaxWriteAttempts; attempt++)
        {
            // Select snapshot by ServiceDate, not AdjudicationTimestamp or today.
            // A claim finalized today for a service date six months ago belongs in the
            // plan year that contained the service date.
            var snapshot = await ResolveSnapshotAsync(evt, ct);
            if (snapshot is null)
                return await OrphanAsync(evt, ct);

            if (await CatchUpAsync(snapshot, ct)) continue;

            // A crashed earlier attempt (whose lease this one took over) may
            // have written the row already: then it is applied — once.
            var already = await _repo.GetClaimAppliedEventAsync(evt.TenantId, evt.ClaimId, ct);
            if (already is not null)
            {
                await _processed.CompleteAsync(evt.TenantId, evt.ClaimId, already.Id, "Applied", ct);
                return new ApplyResult(ApplyOutcome.Duplicate, snapshot, already.Id, "DuplicateClaim");
            }

            var (requestedDeductible, requestedOop, serviceDeltas) = ComputeDeltas(evt);
            var requestedFamilyDeductible = evt.IsFamilyAggregate ? requestedDeductible : 0m;
            var requestedFamilyOop = evt.IsFamilyAggregate ? requestedOop : 0m;

            // The audit row records what was actually applied after clamping at
            // the limits, so a later reversal backs out exactly that.
            var expectedVersion = snapshot.Version;
            var before = (snapshot.IndividualDeductibleUsed, snapshot.IndividualOopUsed,
                snapshot.FamilyDeductibleUsed, snapshot.FamilyOopUsed);
            snapshot.IndividualDeductibleUsed = Clamp(snapshot.IndividualDeductibleUsed + requestedDeductible, snapshot.IndividualDeductibleLimit);
            snapshot.IndividualOopUsed = Clamp(snapshot.IndividualOopUsed + requestedOop, snapshot.IndividualOopLimit);
            snapshot.FamilyDeductibleUsed = Clamp(snapshot.FamilyDeductibleUsed + requestedFamilyDeductible, snapshot.FamilyDeductibleLimit);
            snapshot.FamilyOopUsed = Clamp(snapshot.FamilyOopUsed + requestedFamilyOop, snapshot.FamilyOopLimit);
            var deductibleDelta = snapshot.IndividualDeductibleUsed - before.IndividualDeductibleUsed;
            var oopDelta = snapshot.IndividualOopUsed - before.IndividualOopUsed;
            var familyDeductibleDelta = snapshot.FamilyDeductibleUsed - before.FamilyDeductibleUsed;
            var familyOopDelta = snapshot.FamilyOopUsed - before.FamilyOopUsed;
            ApplyServiceDeltas(snapshot, serviceDeltas);
            snapshot.Version = expectedVersion + 1;

            // Fresh EventId per attempt so a retry (Pending-marker replay) does not
            // collide on the unique (tenantId, eventId) index. Wire-level dedup is
            // ProcessedClaim's job, not this event row's. The row id is one per
            // (snapshot, version): a writer that lost the race conflicts here.
            var auditEvent = new AccumulatorEvent
            {
                Id = AccumulatorEvent.BuildId(snapshot.Id, snapshot.Version),
                TenantId = evt.TenantId,
                EventId = Guid.NewGuid().ToString(),
                AggregateId = snapshot.Id,
                Version = snapshot.Version,
                MemberId = evt.MemberId,
                PlanYearStart = snapshot.PlanYearStart,
                PlanYearEnd = snapshot.PlanYearEnd,
                EventType = "ClaimApplied",
                SourceReference = evt.ClaimId,
                SourceClaimId = evt.ClaimId,
                ActorId = "system",
                DeductibleDelta = deductibleDelta,
                OopDelta = oopDelta,
                FamilyDeductibleDelta = familyDeductibleDelta,
                FamilyOopDelta = familyOopDelta,
                DeltasClamped = true,
                ServiceDeltas = serviceDeltas.Select(d => new ServiceAccumulatorDeltaRow
                {
                    BenefitCategory = d.BenefitCategory,
                    UsedDelta = d.UsedDelta,
                    Unit = d.Unit
                }).ToList(),
                OccurredAt = evt.OccurredAt.UtcDateTime
            };

            if (!await _repo.TryAppendEventAsync(auditEvent, ct)) continue;
            await ProjectAsync(snapshot, expectedVersion, ct);
            await _processed.CompleteAsync(evt.TenantId, evt.ClaimId, auditEvent.Id, "Applied", ct);

            await _publisher.PublishAdjustedAsync(new AccumulatorAdjustedEvent
            {
                TenantId = evt.TenantId,
                MemberId = evt.MemberId,
                PlanYearStart = snapshot.PlanYearStart,
                PlanYearEnd = snapshot.PlanYearEnd,
                AdjustmentSource = "ClaimApplied",
                SourceReference = evt.ClaimId,
                ActorId = "system",
                DeductibleDelta = deductibleDelta,
                OopDelta = oopDelta,
                FamilyDeductibleDelta = familyDeductibleDelta,
                FamilyOopDelta = familyOopDelta,
                ServiceDeltas = serviceDeltas
            }, ct);

            return new ApplyResult(ApplyOutcome.Applied, snapshot, auditEvent.Id, null);
        }

        throw new AccumulatorWriteContentionException(evt.TenantId, evt.ClaimId);
    }

    private async Task<ApplyResult> OrphanAsync(ClaimFinalizedEvent evt, CancellationToken ct)
    {
        _logger.LogWarning(
            "Orphan claim: ServiceDate {ServiceDate} does not map to any known plan-year snapshot for member {MemberId} tenant {TenantId}. ClaimId={ClaimId}",
            evt.ServiceDate, SanitizeForLog(evt.MemberId), SanitizeForLog(evt.TenantId), SanitizeForLog(evt.ClaimId));

        await _publisher.PublishOrphanAsync(new OrphanAccumulatorClaimEvent
        {
            TenantId = evt.TenantId,
            MemberId = evt.MemberId,
            ClaimId = evt.ClaimId,
            ClaimNumber = evt.ClaimNumber,
            ServiceDate = evt.ServiceDate,
            Reason = "No AccumulatorSnapshot matches ServiceDate plan year"
        }, ct);

        await _processed.CompleteAsync(evt.TenantId, evt.ClaimId, resultingEventId: string.Empty, outcome: "OrphanSkipped", ct);
        return new ApplyResult(ApplyOutcome.Orphan, null, null, "OrphanServiceDate");
    }

    public async Task<AccumulatorAdjustmentResponse> AdjustAsync(string tenantId, string memberId, string actorId, AccumulatorAdjustmentRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(actorId))
            throw new ArgumentException("An authenticated actor is required for a manual adjustment.", nameof(actorId));

        // Idempotent replay on client-supplied AdjustmentId: if we've seen this
        // adjustmentId before, return the existing snapshot rather than applying
        // the delta again. Before this check the duplicate index violation would
        // surface as a 500 from the event append.
        if (!string.IsNullOrWhiteSpace(request.AdjustmentId))
        {
            var prior = await _repo.GetManualAdjustmentAsync(tenantId, request.AdjustmentId, ct);
            if (prior is not null)
            {
                var existing = await _repo.GetSnapshotAsync(tenantId, memberId, request.PlanYearStart, ct);
                return new AccumulatorAdjustmentResponse
                {
                    AdjustmentId = request.AdjustmentId,
                    Snapshot = existing ?? new AccumulatorSnapshot
                    {
                        Id = AccumulatorSnapshot.BuildId(tenantId, memberId, request.PlanYearStart),
                        TenantId = tenantId,
                        MemberId = memberId,
                        PlanYearStart = request.PlanYearStart,
                        PlanYearEnd = request.PlanYearEnd
                    }
                };
            }
        }

        var adjustmentId = request.AdjustmentId ?? Guid.NewGuid().ToString();
        for (var attempt = 0; attempt < MaxWriteAttempts; attempt++)
        {
            var snapshot = await _repo.GetSnapshotAsync(tenantId, memberId, request.PlanYearStart, ct)
                ?? new AccumulatorSnapshot
                {
                    Id = AccumulatorSnapshot.BuildId(tenantId, memberId, request.PlanYearStart),
                    TenantId = tenantId,
                    MemberId = memberId,
                    PlanYearStart = request.PlanYearStart,
                    PlanYearEnd = request.PlanYearEnd
                };
            if (await CatchUpAsync(snapshot, ct)) continue;

            var expectedVersion = snapshot.Version;
            snapshot.IndividualDeductibleUsed = Math.Max(0m, snapshot.IndividualDeductibleUsed + request.DeductibleDelta);
            snapshot.IndividualOopUsed = Math.Max(0m, snapshot.IndividualOopUsed + request.OopDelta);
            snapshot.FamilyDeductibleUsed = Math.Max(0m, snapshot.FamilyDeductibleUsed + request.FamilyDeductibleDelta);
            snapshot.FamilyOopUsed = Math.Max(0m, snapshot.FamilyOopUsed + request.FamilyOopDelta);

            foreach (var s in request.ServiceDeltas)
            {
                ApplyOneServiceDelta(snapshot, s.BenefitCategory, s.UsedDelta, s.Unit);
            }
            snapshot.Version = expectedVersion + 1;

            var auditEvent = new AccumulatorEvent
            {
                Id = AccumulatorEvent.BuildId(snapshot.Id, snapshot.Version),
                TenantId = tenantId,
                // Fresh wire-level EventId; duplicate AdjustmentId handled above by
                // GetManualAdjustmentAsync lookup keyed on SourceReference.
                EventId = Guid.NewGuid().ToString(),
                AggregateId = snapshot.Id,
                Version = snapshot.Version,
                MemberId = memberId,
                PlanYearStart = snapshot.PlanYearStart,
                PlanYearEnd = snapshot.PlanYearEnd,
                EventType = "ManualAdjustment",
                SourceReference = adjustmentId,
                ActorId = actorId,
                Reason = request.Reason,
                DeductibleDelta = request.DeductibleDelta,
                OopDelta = request.OopDelta,
                FamilyDeductibleDelta = request.FamilyDeductibleDelta,
                FamilyOopDelta = request.FamilyOopDelta,
                ServiceDeltas = request.ServiceDeltas.Select(d => new ServiceAccumulatorDeltaRow
                {
                    BenefitCategory = d.BenefitCategory,
                    UsedDelta = d.UsedDelta,
                    Unit = d.Unit
                }).ToList()
            };

            if (!await _repo.TryAppendEventAsync(auditEvent, ct)) continue;
            await ProjectAsync(snapshot, expectedVersion, ct);

            await _publisher.PublishAdjustedAsync(new AccumulatorAdjustedEvent
            {
                TenantId = tenantId,
                MemberId = memberId,
                PlanYearStart = snapshot.PlanYearStart,
                PlanYearEnd = snapshot.PlanYearEnd,
                AdjustmentSource = "ManualAdjustment",
                SourceReference = adjustmentId,
                ActorId = actorId,
                Reason = request.Reason,
                DeductibleDelta = request.DeductibleDelta,
                OopDelta = request.OopDelta,
                FamilyDeductibleDelta = request.FamilyDeductibleDelta,
                FamilyOopDelta = request.FamilyOopDelta,
                ServiceDeltas = request.ServiceDeltas.Select(d => new ServiceAccumulatorDelta
                {
                    BenefitCategory = d.BenefitCategory,
                    UsedDelta = d.UsedDelta,
                    Unit = d.Unit
                }).ToList()
            }, ct);

            return new AccumulatorAdjustmentResponse { AdjustmentId = adjustmentId, Snapshot = snapshot };
        }

        throw new AccumulatorWriteContentionException(tenantId, adjustmentId);
    }

    // ── versioned writes ────────────────────────────────────────────────
    //
    // Every write is: read the snapshot (version v) → append the event row
    // for version v+1 (its id is one per snapshot+version, so a writer that
    // lost the race gets a conflict and retries from a fresh read) → replace
    // the snapshot only if it is still at v. The event row is the truth: a
    // writer that crashes between the append and the snapshot write leaves
    // a row above the snapshot's version, and the next writer projects it
    // (CatchUpAsync) before writing its own — nothing is lost or counted
    // twice, whichever worker gets there.

    /// <summary>
    /// Projects onto <paramref name="snapshot"/> the event rows above its
    /// version (a crashed writer's) and writes it. Returns true when there
    /// were any — the caller re-reads before writing its own row.
    /// </summary>
    private async Task<bool> CatchUpAsync(AccumulatorSnapshot snapshot, CancellationToken ct)
    {
        var pending = await _repo.GetAggregateEventsAsync(snapshot.TenantId, snapshot.Id, snapshot.Version, ct);
        if (pending.Count == 0) return false;

        var expectedVersion = snapshot.Version;
        foreach (var row in pending.OrderBy(e => e.Version))
        {
            if (row.Version != snapshot.Version + 1) break;
            ProjectRow(snapshot, row);
            snapshot.Version = row.Version;
        }
        _logger.LogWarning(
            "Accumulator snapshot {SnapshotId} was behind its event log (v{From} → v{To}); projected the missing rows",
            SanitizeForLog(snapshot.Id), expectedVersion, snapshot.Version);
        await _repo.TryReplaceSnapshotAsync(snapshot, expectedVersion, ct);
        return true;
    }

    /// <summary>
    /// Writes the snapshot after this writer's row was appended. A false
    /// return means another worker already projected the row (CatchUpAsync)
    /// — the row is the truth either way.
    /// </summary>
    private async Task ProjectAsync(AccumulatorSnapshot snapshot, long expectedVersion, CancellationToken ct)
    {
        if (!await _repo.TryReplaceSnapshotAsync(snapshot, expectedVersion, ct))
        {
            _logger.LogInformation(
                "Accumulator snapshot {SnapshotId} v{Version} was already projected by another worker",
                SanitizeForLog(snapshot.Id), snapshot.Version);
        }
    }

    /// <summary>Applies one event row's recorded deltas the way its writer applied them.</summary>
    private static void ProjectRow(AccumulatorSnapshot snapshot, AccumulatorEvent row)
    {
        if (row.EventType == "ClaimApplied" && !row.DeltasClamped)
        {
            // Legacy row: the requested amounts, clamped at the limits when written.
            snapshot.IndividualDeductibleUsed = Clamp(snapshot.IndividualDeductibleUsed + row.DeductibleDelta, snapshot.IndividualDeductibleLimit);
            snapshot.IndividualOopUsed = Clamp(snapshot.IndividualOopUsed + row.OopDelta, snapshot.IndividualOopLimit);
            snapshot.FamilyDeductibleUsed = Clamp(snapshot.FamilyDeductibleUsed + row.FamilyDeductibleDelta, snapshot.FamilyDeductibleLimit);
            snapshot.FamilyOopUsed = Clamp(snapshot.FamilyOopUsed + row.FamilyOopDelta, snapshot.FamilyOopLimit);
        }
        else
        {
            snapshot.IndividualDeductibleUsed = Math.Max(0m, snapshot.IndividualDeductibleUsed + row.DeductibleDelta);
            snapshot.IndividualOopUsed = Math.Max(0m, snapshot.IndividualOopUsed + row.OopDelta);
            snapshot.FamilyDeductibleUsed = Math.Max(0m, snapshot.FamilyDeductibleUsed + row.FamilyDeductibleDelta);
            snapshot.FamilyOopUsed = Math.Max(0m, snapshot.FamilyOopUsed + row.FamilyOopDelta);
        }
        foreach (var d in row.ServiceDeltas)
            ApplyOneServiceDelta(snapshot, d.BenefitCategory, d.UsedDelta, d.Unit);
    }

    // ── reversal ────────────────────────────────────────────────────────

    private static bool IsReversal(string? finalStatus) =>
        string.Equals(finalStatus, "Reversed", StringComparison.OrdinalIgnoreCase)
        || string.Equals(finalStatus, "Voided", StringComparison.OrdinalIgnoreCase);

    private static bool IsDenied(string? finalStatus) =>
        string.Equals(finalStatus, "Denied", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Marker outcome of a claim whose reversal arrived before its apply
    /// (a replacement keyed on another claim id, on another Kafka partition,
    /// overtook the original): the apply that follows is skipped.
    /// </summary>
    public const string ReversedBeforeApplyOutcome = "ReversedBeforeApply";

    /// <summary>Marker outcome of a denied claim: nothing applied.</summary>
    public const string DeniedOutcome = "DeniedNotApplied";

    private async Task<ApplyResult> SkipDeniedAsync(ClaimFinalizedEvent evt, CancellationToken ct)
    {
        var begin = await _processed.TryBeginAsync(evt.TenantId, evt.ClaimId, ct);
        switch (begin)
        {
            case BeginClaimOutcome.InProgress:
                return new ApplyResult(ApplyOutcome.InProgress, null, null, "ApplyInProgress");
            case BeginClaimOutcome.Proceed:
                await _processed.CompleteAsync(evt.TenantId, evt.ClaimId, string.Empty, DeniedOutcome, ct);
                return new ApplyResult(ApplyOutcome.Skipped, null, null, DeniedOutcome);
        }

        // Already processed: if it applied, the denial backs that out.
        var marker = await _processed.GetAsync(evt.TenantId, evt.ClaimId, ct);
        if (marker?.Outcome == "Applied")
            return await ReverseClaimAsync(evt.TenantId, evt.ClaimId, evt.ClaimId, ct);
        return new ApplyResult(ApplyOutcome.Duplicate, null, null, marker?.Outcome ?? DeniedOutcome);
    }

    /// <summary>
    /// Backs out the deltas <paramref name="claimId"/> applied (its
    /// <c>ClaimApplied</c> audit row — the amounts actually applied after
    /// clamping; for a legacy row, replayed from the event log), records a
    /// <c>ClaimReversed</c> row and publishes the negative adjustment.
    /// Exactly once: idempotent on <c>{claimId}:reversal</c> (a void event
    /// and a replacement naming the same original reverse it once), an
    /// attempt in flight makes a concurrent one retry later, and the
    /// existing-reversal check runs under the versioned write, so even two
    /// workers that both got past the marker write one reversal (Mongo also
    /// has a unique index on it). A claim that never applied reverses nothing.
    /// </summary>
    private async Task<ApplyResult> ReverseClaimAsync(
        string tenantId, string claimId, string sourceReference, CancellationToken ct)
    {
        var key = claimId + ":reversal";
        var begin = await _processed.TryBeginAsync(tenantId, key, ct);
        if (begin == BeginClaimOutcome.AlreadyApplied)
            return new ApplyResult(ApplyOutcome.Duplicate, null, null, "DuplicateReversal");
        if (begin == BeginClaimOutcome.InProgress)
            return new ApplyResult(ApplyOutcome.InProgress, null, null, "ReversalInProgress");

        for (var attempt = 0; attempt < MaxWriteAttempts; attempt++)
        {
            var applied = await _repo.GetClaimAppliedEventAsync(tenantId, claimId, ct);
            var snapshot = applied is null
                ? null
                : await _repo.GetSnapshotAsync(tenantId, applied.MemberId, applied.PlanYearStart, ct);
            if (applied is null)
            {
                // Round 3 (B3): nothing applied *yet* is not nothing to
                // reverse. Kafka is keyed on the claim id, so a replacement
                // (another key, another partition) can overtake its
                // original's apply. Completing this reversal as "nothing to
                // reverse" let the original apply afterwards and count on top
                // of the replacement (deductible 600 instead of 300).
                var original = await _processed.GetAsync(tenantId, claimId, ct);
                if (original is { Outcome: "Pending" })
                {
                    // The original's apply is in flight: retry once it lands.
                    await _processed.ReleaseAsync(tenantId, key, ct);
                    return new ApplyResult(ApplyOutcome.InProgress, null, null, "OriginalApplyInProgress");
                }

                if (original is null)
                {
                    // Never seen: leave a tombstone so its apply is skipped.
                    switch (await _processed.TryBeginAsync(tenantId, claimId, ct))
                    {
                        case BeginClaimOutcome.Proceed:
                            await _processed.CompleteAsync(tenantId, claimId, string.Empty, ReversedBeforeApplyOutcome, ct);
                            await _processed.CompleteAsync(tenantId, key, string.Empty, ReversedBeforeApplyOutcome, ct);
                            _logger.LogInformation(
                                "Reversal for claim {ClaimId} tenant {TenantId} arrived before its apply; " +
                                "the apply will be skipped",
                                SanitizeForLog(claimId), SanitizeForLog(tenantId));
                            return new ApplyResult(ApplyOutcome.Duplicate, null, null, ReversedBeforeApplyOutcome);
                        case BeginClaimOutcome.InProgress:
                            // The apply started meanwhile.
                            await _processed.ReleaseAsync(tenantId, key, ct);
                            return new ApplyResult(ApplyOutcome.InProgress, null, null, "OriginalApplyInProgress");
                        default:
                            // It completed meanwhile: re-read its row.
                            continue;
                    }
                }

                // Terminal without a row (orphan, denied, already tombstoned):
                // it never counted and never will.
                _logger.LogInformation(
                    "Reversal for claim {ClaimId} tenant {TenantId}: nothing applied ({Outcome}); nothing to reverse",
                    SanitizeForLog(claimId), SanitizeForLog(tenantId), original.Outcome);
                await _processed.CompleteAsync(tenantId, key, string.Empty, "NothingToReverse", ct);
                return new ApplyResult(ApplyOutcome.Duplicate, null, null, "NothingToReverse");
            }

            if (snapshot is null)
            {
                _logger.LogWarning(
                    "Reversal for claim {ClaimId} tenant {TenantId}: its snapshot is gone; nothing to reverse",
                    SanitizeForLog(claimId), SanitizeForLog(tenantId));
                await _processed.CompleteAsync(tenantId, key, string.Empty, "NothingToReverse", ct);
                return new ApplyResult(ApplyOutcome.Duplicate, null, null, "NothingToReverse");
            }

            if (await CatchUpAsync(snapshot, ct)) continue;

            var existingReversal = await _repo.GetClaimReversedEventAsync(tenantId, claimId, ct);
            if (existingReversal is not null)
            {
                await _processed.CompleteAsync(tenantId, key, existingReversal.Id, "Reversed", ct);
                return new ApplyResult(ApplyOutcome.Duplicate, snapshot, existingReversal.Id, "DuplicateReversal");
            }

            var amounts = applied.DeltasClamped
                ? new AppliedAmounts(applied.DeductibleDelta, applied.OopDelta, applied.FamilyDeductibleDelta, applied.FamilyOopDelta)
                : await ReplayLegacyRowAsync(snapshot, applied, ct);

            var expectedVersion = snapshot.Version;
            var before = (snapshot.IndividualDeductibleUsed, snapshot.IndividualOopUsed,
                snapshot.FamilyDeductibleUsed, snapshot.FamilyOopUsed);
            snapshot.IndividualDeductibleUsed = Math.Max(0m, snapshot.IndividualDeductibleUsed - amounts.Deductible);
            snapshot.IndividualOopUsed = Math.Max(0m, snapshot.IndividualOopUsed - amounts.Oop);
            snapshot.FamilyDeductibleUsed = Math.Max(0m, snapshot.FamilyDeductibleUsed - amounts.FamilyDeductible);
            snapshot.FamilyOopUsed = Math.Max(0m, snapshot.FamilyOopUsed - amounts.FamilyOop);
            var serviceDeltas = applied.ServiceDeltas
                .Select(d => new ServiceAccumulatorDelta { BenefitCategory = d.BenefitCategory, UsedDelta = -d.UsedDelta, Unit = d.Unit })
                .ToList();
            ApplyServiceDeltas(snapshot, serviceDeltas);
            snapshot.Version = expectedVersion + 1;

            var reversal = new AccumulatorEvent
            {
                Id = AccumulatorEvent.BuildId(snapshot.Id, snapshot.Version),
                TenantId = tenantId,
                EventId = Guid.NewGuid().ToString(),
                AggregateId = snapshot.Id,
                Version = snapshot.Version,
                MemberId = applied.MemberId,
                PlanYearStart = snapshot.PlanYearStart,
                PlanYearEnd = snapshot.PlanYearEnd,
                EventType = "ClaimReversed",
                SourceReference = sourceReference,
                SourceClaimId = claimId,
                ActorId = "system",
                DeductibleDelta = snapshot.IndividualDeductibleUsed - before.IndividualDeductibleUsed,
                OopDelta = snapshot.IndividualOopUsed - before.IndividualOopUsed,
                FamilyDeductibleDelta = snapshot.FamilyDeductibleUsed - before.FamilyDeductibleUsed,
                FamilyOopDelta = snapshot.FamilyOopUsed - before.FamilyOopUsed,
                DeltasClamped = true,
                ServiceDeltas = serviceDeltas.Select(d => new ServiceAccumulatorDeltaRow
                {
                    BenefitCategory = d.BenefitCategory, UsedDelta = d.UsedDelta, Unit = d.Unit
                }).ToList(),
                OccurredAt = DateTime.UtcNow
            };

            // A conflict is a lost version race or (Mongo) another worker's
            // reversal of this claim: re-read; the check above then sees it.
            if (!await _repo.TryAppendEventAsync(reversal, ct)) continue;
            await ProjectAsync(snapshot, expectedVersion, ct);
            await _processed.CompleteAsync(tenantId, key, reversal.Id, "Reversed", ct);

            await _publisher.PublishAdjustedAsync(new AccumulatorAdjustedEvent
            {
                TenantId = tenantId,
                MemberId = applied.MemberId,
                PlanYearStart = snapshot.PlanYearStart,
                PlanYearEnd = snapshot.PlanYearEnd,
                AdjustmentSource = "ClaimReversed",
                SourceReference = sourceReference,
                ActorId = "system",
                DeductibleDelta = reversal.DeductibleDelta,
                OopDelta = reversal.OopDelta,
                FamilyDeductibleDelta = reversal.FamilyDeductibleDelta,
                FamilyOopDelta = reversal.FamilyOopDelta,
                ServiceDeltas = serviceDeltas
            }, ct);

            return new ApplyResult(ApplyOutcome.Applied, snapshot, reversal.Id, "Reversed");
        }

        throw new AccumulatorWriteContentionException(tenantId, key);
    }

    private readonly record struct AppliedAmounts(decimal Deductible, decimal Oop, decimal FamilyDeductible, decimal FamilyOop);

    /// <summary>
    /// A legacy <c>ClaimApplied</c> row (written before rows recorded the
    /// clamped amounts) holds what the claim requested, which can exceed what
    /// the snapshot took when it hit a limit: limit 500, used 450, a claim
    /// requesting 200 took 50 but its row says 200, and reversing 200 would
    /// take 150 of other claims' deductible with it. Replays the snapshot's
    /// event log to find what the row actually applied.
    /// <para>The log's starting value is solved for: the smallest starting
    /// value from which the replay reproduces the current snapshot (rows
    /// clamped at the current limits). Limits that changed since a row was
    /// written, or used amounts loaded into a snapshot outside the log, can
    /// make that inexact; the result is always between 0 and the recorded
    /// amount, so a reversal never takes more than the row recorded.</para>
    /// </summary>
    private async Task<AppliedAmounts> ReplayLegacyRowAsync(
        AccumulatorSnapshot snapshot, AccumulatorEvent target, CancellationToken ct)
    {
        var log = (await _repo.GetAggregateEventsAsync(snapshot.TenantId, snapshot.Id, 0, ct))
            .Where(e => e.Version <= snapshot.Version)
            .OrderBy(e => e.Version)
            .ToList();
        if (!log.Any(e => e.Id == target.Id))
        {
            // Not in this snapshot's log (should not happen): trust nothing
            // beyond what the snapshot can give back.
            return new AppliedAmounts(
                Math.Min(target.DeductibleDelta, snapshot.IndividualDeductibleUsed),
                Math.Min(target.OopDelta, snapshot.IndividualOopUsed),
                Math.Min(target.FamilyDeductibleDelta, snapshot.FamilyDeductibleUsed),
                Math.Min(target.FamilyOopDelta, snapshot.FamilyOopUsed));
        }

        _logger.LogInformation(
            "Reversing legacy ClaimApplied row {EventId} (claim {ClaimId}): replaying {Count} event(s) of snapshot {SnapshotId}",
            SanitizeForLog(target.Id), SanitizeForLog(target.SourceClaimId), log.Count, SanitizeForLog(snapshot.Id));

        return new AppliedAmounts(
            Replay(log, target, snapshot.IndividualDeductibleUsed, snapshot.IndividualDeductibleLimit, e => e.DeductibleDelta),
            Replay(log, target, snapshot.IndividualOopUsed, snapshot.IndividualOopLimit, e => e.OopDelta),
            Replay(log, target, snapshot.FamilyDeductibleUsed, snapshot.FamilyDeductibleLimit, e => e.FamilyDeductibleDelta),
            Replay(log, target, snapshot.FamilyOopUsed, snapshot.FamilyOopLimit, e => e.FamilyOopDelta));
    }

    /// <summary>One counter of <see cref="ReplayLegacyRowAsync"/>: what <paramref name="target"/> applied.</summary>
    public static decimal Replay(
        IReadOnlyList<AccumulatorEvent> log, AccumulatorEvent target, decimal current, decimal limit,
        Func<AccumulatorEvent, decimal> delta)
    {
        (decimal Final, decimal TargetApplied) Run(decimal start)
        {
            var used = start;
            var targetApplied = 0m;
            foreach (var row in log)
            {
                var before = used;
                used = row.EventType == "ClaimApplied" && !row.DeltasClamped
                    ? Clamp(used + delta(row), limit)
                    : Math.Max(0m, used + delta(row));
                if (row.Id == target.Id) targetApplied = used - before;
            }
            return (used, targetApplied);
        }

        // The replay's end value rises with the starting value, by at most
        // as much: step the start up by the shortfall until it reproduces
        // the snapshot (or stops moving).
        var start = Math.Max(0m, current - log.Sum(delta));
        var run = Run(start);
        for (var i = 0; i < 100 && run.Final != current; i++)
        {
            var next = Math.Max(0m, start + (current - run.Final));
            if (next == start) break;
            start = next;
            run = Run(start);
        }

        var recorded = delta(target);
        return recorded >= 0m
            ? Math.Clamp(run.TargetApplied, 0m, recorded)
            : Math.Clamp(run.TargetApplied, recorded, 0m);
    }

    // ── helpers ─────────────────────────────────────────────────────────

    private async Task<AccumulatorSnapshot?> ResolveSnapshotAsync(ClaimFinalizedEvent evt, CancellationToken ct)
    {
        // Prefer the snapshot that explicitly covers the service date; fall back to
        // one keyed by the event's PlanYearStart if both exist (producer may have
        // pre-resolved the plan year).
        var byServiceDate = await _repo.GetSnapshotByAsOfDateAsync(evt.TenantId, evt.MemberId, evt.ServiceDate, ct);
        if (byServiceDate is not null) return byServiceDate;

        if (evt.PlanYearStart != default)
        {
            var byKey = await _repo.GetSnapshotAsync(evt.TenantId, evt.MemberId, evt.PlanYearStart, ct);
            if (byKey is not null) return byKey;

            // Producer-asserted plan year but no snapshot yet — create an empty
            // snapshot to hold the amounts rather than dropping them as orphan.
            // Limits default to zero; benefit-plan-service will hydrate limits
            // later via a separate workflow.
            if (evt.PlanYearStart <= evt.ServiceDate && evt.PlanYearEnd >= evt.ServiceDate)
            {
                var fresh = new AccumulatorSnapshot
                {
                    Id = AccumulatorSnapshot.BuildId(evt.TenantId, evt.MemberId, evt.PlanYearStart),
                    TenantId = evt.TenantId,
                    MemberId = evt.MemberId,
                    PlanYearStart = evt.PlanYearStart,
                    PlanYearEnd = evt.PlanYearEnd
                };
                return fresh;
            }
        }

        return null;
    }

    internal static (decimal deductible, decimal oop, List<ServiceAccumulatorDelta> services) ComputeDeltas(ClaimFinalizedEvent evt)
    {
        // Prefer per-line amounts when present — they let us attribute to multiple
        // benefit categories from a single claim. Fall back to claim-level when
        // the producer didn't populate lines.
        if (evt.LineItems.Count > 0)
        {
            // The deductible accumulator takes the credited deductible when
            // the producer sent one (a plan paying secondary or later with
            // NAIC full credit); the service rollup below stays the member's
            // cost share.
            var deductible = evt.LineItems.Sum(l => l.DeductibleCredited ?? l.DeductibleApplied);
            var oop = evt.LineItems.Sum(l => l.OopApplied);
            var services = evt.LineItems
                .GroupBy(l => string.IsNullOrWhiteSpace(l.BenefitCategory) ? evt.BenefitCategory : l.BenefitCategory)
                .Where(g => !string.IsNullOrWhiteSpace(g.Key))
                .Select(g => new ServiceAccumulatorDelta
                {
                    BenefitCategory = g.Key,
                    UsedDelta = g.Sum(l => l.DeductibleApplied + l.CoinsuranceApplied + l.CopayApplied),
                    Unit = "USD"
                })
                .ToList();
            return (deductible, oop, services);
        }

        var categoryRollup = new List<ServiceAccumulatorDelta>();
        if (!string.IsNullOrWhiteSpace(evt.BenefitCategory))
        {
            categoryRollup.Add(new ServiceAccumulatorDelta
            {
                BenefitCategory = evt.BenefitCategory,
                UsedDelta = evt.DeductibleApplied + evt.CoinsuranceApplied + evt.CopayApplied,
                Unit = "USD"
            });
        }
        return (evt.DeductibleCredited ?? evt.DeductibleApplied, evt.OopApplied, categoryRollup);
    }

    private static void ApplyServiceDeltas(AccumulatorSnapshot snapshot, List<ServiceAccumulatorDelta> deltas)
    {
        foreach (var d in deltas)
        {
            ApplyOneServiceDelta(snapshot, d.BenefitCategory, d.UsedDelta, d.Unit);
        }
    }

    private static void ApplyOneServiceDelta(AccumulatorSnapshot snapshot, string category, decimal used, string unit)
    {
        if (string.IsNullOrWhiteSpace(category)) return;
        var existing = snapshot.ServiceAccumulators.FirstOrDefault(s =>
            string.Equals(s.BenefitCategory, category, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            snapshot.ServiceAccumulators.Add(new ServiceAccumulator
            {
                BenefitCategory = category,
                Used = Math.Max(0m, used),
                Unit = unit
            });
            return;
        }
        existing.Used = Math.Max(0m, existing.Used + used);
    }

    /// <summary>
    /// OOP/deductible counters are always ≥ 0 and, when a limit is set, ≤ the limit.
    /// Exceeding the limit is a legitimate edge (retro-finalize + coverage change),
    /// so we cap rather than reject. Anything truly anomalous surfaces in the event
    /// stream at full fidelity — the snapshot is a projection, the event store is truth.
    /// </summary>
    private static decimal Clamp(decimal value, decimal limit)
    {
        if (value < 0m) return 0m;
        if (limit > 0m && value > limit) return limit;
        return value;
    }

    private static AccumulatorResponse ToResponse(AccumulatorSnapshot s, IReadOnlyList<AccumulatorEvent> recent) => new()
    {
        MemberId = s.MemberId,
        PlanYearStart = s.PlanYearStart,
        PlanYearEnd = s.PlanYearEnd,
        IndividualDeductibleUsed = s.IndividualDeductibleUsed,
        IndividualDeductibleLimit = s.IndividualDeductibleLimit,
        FamilyDeductibleUsed = s.FamilyDeductibleUsed,
        FamilyDeductibleLimit = s.FamilyDeductibleLimit,
        IndividualOopUsed = s.IndividualOopUsed,
        IndividualOopLimit = s.IndividualOopLimit,
        FamilyOopUsed = s.FamilyOopUsed,
        FamilyOopLimit = s.FamilyOopLimit,
        ServiceAccumulators = s.ServiceAccumulators.Select(a => new ServiceAccumulatorDto
        {
            BenefitCategory = a.BenefitCategory,
            Used = a.Used,
            Limit = a.Limit,
            Unit = a.Unit
        }).ToList(),
        RecentActivity = recent.Select(ToActivity).ToList()
    };

    private static AccumulatorActivityDto ToActivity(AccumulatorEvent e) => new()
    {
        EventId = e.EventId,
        EventType = e.EventType,
        SourceReference = e.SourceReference,
        OccurredAt = e.OccurredAt,
        DeductibleDelta = e.DeductibleDelta,
        OopDelta = e.OopDelta,
        FamilyDeductibleDelta = e.FamilyDeductibleDelta,
        FamilyOopDelta = e.FamilyOopDelta,
        Reason = e.Reason,
        ActorId = e.ActorId
    };

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
    }
}

/// <summary>
/// A write lost the version race <see cref="AccumulatorService.MaxWriteAttempts"/>
/// times in a row. Nothing was written; the Kafka consumer does not commit
/// the offset and the message is retried.
/// </summary>
public sealed class AccumulatorWriteContentionException(string tenantId, string key)
    : Exception($"Accumulator write for {key} (tenant {tenantId}) kept losing the snapshot version race; retry later.");
