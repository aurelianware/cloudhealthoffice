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

    public async Task<ApplyResult> ApplyClaimFinalizedAsync(ClaimFinalizedEvent evt, CancellationToken ct = default)
    {
        // Two-phase idempotency — (tenantId, claimId) is the dedupe key even across
        // regenerated EventIds (re-finalization must not double-count). A Pending
        // marker from a crashed prior attempt does NOT block retry: TryBeginAsync
        // returns Proceed in that case so we don't permanently skip on transient
        // failures between the begin-marker and the final CompleteAsync.
        var begin = await _processed.TryBeginAsync(evt.TenantId, evt.ClaimId, ct);
        if (begin == BeginClaimOutcome.AlreadyApplied)
        {
            _logger.LogInformation(
                "ClaimFinalizedEvent for claim {ClaimId} tenant {TenantId} already applied; skipping",
                SanitizeForLog(evt.ClaimId), SanitizeForLog(evt.TenantId));
            return new ApplyResult(ApplyOutcome.Duplicate, null, null, "DuplicateClaim");
        }

        // Select snapshot by ServiceDate, not AdjudicationTimestamp or today.
        // A claim finalized today for a service date six months ago belongs in the
        // plan year that contained the service date.
        var snapshot = await ResolveSnapshotAsync(evt, ct);
        if (snapshot is null)
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

        var (deductibleDelta, oopDelta, serviceDeltas) = ComputeDeltas(evt);
        var familyDeductibleDelta = evt.IsFamilyAggregate ? deductibleDelta : 0m;
        var familyOopDelta = evt.IsFamilyAggregate ? oopDelta : 0m;

        snapshot.IndividualDeductibleUsed = Clamp(snapshot.IndividualDeductibleUsed + deductibleDelta, snapshot.IndividualDeductibleLimit);
        snapshot.IndividualOopUsed = Clamp(snapshot.IndividualOopUsed + oopDelta, snapshot.IndividualOopLimit);
        snapshot.FamilyDeductibleUsed = Clamp(snapshot.FamilyDeductibleUsed + familyDeductibleDelta, snapshot.FamilyDeductibleLimit);
        snapshot.FamilyOopUsed = Clamp(snapshot.FamilyOopUsed + familyOopDelta, snapshot.FamilyOopLimit);
        ApplyServiceDeltas(snapshot, serviceDeltas);
        snapshot.Version += 1;

        // Fresh EventId per attempt so a retry (Pending-marker replay) does not
        // collide on the unique (tenantId, eventId) index. Wire-level dedup is
        // ProcessedClaim's job, not this event row's.
        var auditEvent = new AccumulatorEvent
        {
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
            ServiceDeltas = serviceDeltas.Select(d => new ServiceAccumulatorDeltaRow
            {
                BenefitCategory = d.BenefitCategory,
                UsedDelta = d.UsedDelta,
                Unit = d.Unit
            }).ToList(),
            OccurredAt = evt.OccurredAt.UtcDateTime
        };

        await _repo.AppendEventAsync(auditEvent, ct);
        await _repo.UpsertSnapshotAsync(snapshot, ct);
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

    public async Task<AccumulatorAdjustmentResponse> AdjustAsync(string tenantId, string memberId, AccumulatorAdjustmentRequest request, CancellationToken ct = default)
    {
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

        var snapshot = await _repo.GetSnapshotAsync(tenantId, memberId, request.PlanYearStart, ct)
            ?? new AccumulatorSnapshot
            {
                Id = AccumulatorSnapshot.BuildId(tenantId, memberId, request.PlanYearStart),
                TenantId = tenantId,
                MemberId = memberId,
                PlanYearStart = request.PlanYearStart,
                PlanYearEnd = request.PlanYearEnd
            };

        snapshot.IndividualDeductibleUsed = Math.Max(0m, snapshot.IndividualDeductibleUsed + request.DeductibleDelta);
        snapshot.IndividualOopUsed = Math.Max(0m, snapshot.IndividualOopUsed + request.OopDelta);
        snapshot.FamilyDeductibleUsed = Math.Max(0m, snapshot.FamilyDeductibleUsed + request.FamilyDeductibleDelta);
        snapshot.FamilyOopUsed = Math.Max(0m, snapshot.FamilyOopUsed + request.FamilyOopDelta);

        foreach (var s in request.ServiceDeltas)
        {
            ApplyOneServiceDelta(snapshot, s.BenefitCategory, s.UsedDelta, s.Unit);
        }
        snapshot.Version += 1;

        var adjustmentId = request.AdjustmentId ?? Guid.NewGuid().ToString();
        var auditEvent = new AccumulatorEvent
        {
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
            ActorId = request.ActorId,
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

        await _repo.AppendEventAsync(auditEvent, ct);
        await _repo.UpsertSnapshotAsync(snapshot, ct);

        await _publisher.PublishAdjustedAsync(new AccumulatorAdjustedEvent
        {
            TenantId = tenantId,
            MemberId = memberId,
            PlanYearStart = snapshot.PlanYearStart,
            PlanYearEnd = snapshot.PlanYearEnd,
            AdjustmentSource = "ManualAdjustment",
            SourceReference = adjustmentId,
            ActorId = request.ActorId,
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

    private static (decimal deductible, decimal oop, List<ServiceAccumulatorDelta> services) ComputeDeltas(ClaimFinalizedEvent evt)
    {
        // Prefer per-line amounts when present — they let us attribute to multiple
        // benefit categories from a single claim. Fall back to claim-level when
        // the producer didn't populate lines.
        if (evt.LineItems.Count > 0)
        {
            var deductible = evt.LineItems.Sum(l => l.DeductibleApplied);
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
        return (evt.DeductibleApplied, evt.OopApplied, categoryRollup);
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
