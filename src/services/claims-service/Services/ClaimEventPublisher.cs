using System.Text.Json;
using ClaimsService.Models;
using ClaimsService.Repositories;
using CloudHealthOffice.Events;
using CloudHealthOffice.Events.Kafka;

namespace ClaimsService.Services;

/// <summary>
/// Publishes claim lifecycle events to Kafka through the shared
/// <see cref="IChoEventProducer"/> (idempotent, acks=all). Each event is its
/// own type with its own topic, so there is no shared envelope schema to break.
/// </summary>
public interface IClaimEventPublisher
{
    /// <summary>
    /// Emit a ClaimPendedEvent (advisory, for claims-examiner-service). Safe
    /// to call when Kafka is unavailable: a failure is logged, never thrown.
    /// </summary>
    Task PublishClaimPendedAsync(Claim claim, string tenantId, CancellationToken ct = default);

    /// <summary>
    /// The <c>claims.finalized.v1</c> event for <paramref name="claim"/> as
    /// it stands now, as an outbox entry. The caller appends it to
    /// <see cref="Claim.PendingClaimEvents"/> (<see cref="ClaimEventOutboxExtensions.AddPendingClaimEvent"/>)
    /// before the write that changes the claim's status, so the event and the
    /// state change are stored together or not at all.
    /// <paramref name="finalStatus"/> overrides the label derived from the
    /// claim's status (a void passes "Reversed" before its write).
    /// Null when Kafka is not configured (nothing would ever publish it).
    /// </summary>
    ClaimEventOutboxItem? PrepareClaimFinalized(Claim claim, string tenantId, string? finalStatus = null);

    /// <summary>
    /// After that write: publishes the claim's pending events, oldest first,
    /// and removes each one Kafka acknowledges. Never throws. An event that is
    /// not acknowledged stays in the outbox, and <c>ClaimEventOutboxDispatcher</c>
    /// retries it, so accumulator-service and analytics cannot silently miss it.
    /// </summary>
    Task PublishClaimFinalizedAsync(Claim claim, string tenantId, CancellationToken ct = default);
}

/// <summary>Outbox helpers on <see cref="Claim"/>.</summary>
public static class ClaimEventOutboxExtensions
{
    /// <summary>Appends <paramref name="item"/> (when there is one) to the claim's pending events.</summary>
    public static void AddPendingClaimEvent(this Claim claim, ClaimEventOutboxItem? item)
    {
        if (item is null) return;
        (claim.PendingClaimEvents ??= new List<ClaimEventOutboxItem>()).Add(item);
    }
}

public class ClaimEventPublisher : IClaimEventPublisher, IClaimWorkEventPublisher
{
    public const string ClaimPendedTopic = "claims.pended.v1";
    public const string ClaimFinalizedTopic = "claims.finalized.v1";

    /// <summary>Claim work needed / resolved, consumed by work-management-service.</summary>
    public const string ClaimWorkTopic = ClaimWorkEventTopics.Topic;

    private readonly IChoEventProducer _producer;
    private readonly ClaimEventOutboxDrainer _drainer;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<ClaimEventPublisher> _logger;
    private readonly TimeProvider _clock;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public ClaimEventPublisher(
        IChoEventProducer producer, ClaimEventOutboxDrainer drainer, IServiceScopeFactory scopes,
        ILogger<ClaimEventPublisher> logger, TimeProvider? clock = null)
    {
        _producer = producer;
        _drainer = drainer;
        _scopes = scopes;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
        if (!producer.IsConfigured)
            _logger.LogWarning("Kafka:BootstrapServers not configured — claim events are not queued or published");
    }

    public async Task PublishClaimPendedAsync(Claim claim, string tenantId, CancellationToken ct = default)
    {
        if (!_producer.IsConfigured)
        {
            _logger.LogDebug("Kafka not configured; skipping ClaimPendedEvent for claim {ClaimId}", claim.Id);
            return;
        }

        var evt = new ClaimPendedEvent
        {
            TenantId = tenantId,
            ClaimId = claim.Id,
            ClaimNumber = claim.ClaimNumber,
            MemberId = claim.MemberId,
            BillingProviderNPI = claim.BillingProviderNPI,
            LineOfBusiness = claim.LineOfBusiness.ToString(),
            TotalChargeAmount = claim.TotalChargeAmount,
            ServiceDateFrom = claim.ServiceDateFrom,
            PendDetails = MapPendDetails(claim.PendDetails)
        };
        var headers = new Dictionary<string, string>
        {
            ["tenant-id"] = tenantId,
            ["event-type"] = evt.EventType,
            ["event-version"] = evt.EventVersion,
        };

        try
        {
            await _producer.ProduceAsync(ClaimPendedTopic, claim.Id, JsonSerializer.Serialize(evt, JsonOptions), headers, ct);
            _logger.LogInformation("Published ClaimPendedEvent for claim {ClaimId} to {Topic}", claim.Id, ClaimPendedTopic);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogError(ex, "Failed to publish ClaimPendedEvent for claim {ClaimId}", claim.Id);
        }
    }

    public ClaimEventOutboxItem? PrepareClaimFinalized(Claim claim, string tenantId, string? finalStatus = null)
    {
        if (!_producer.IsConfigured) return null;

        var evt = BuildFinalizedEvent(claim, tenantId);
        if (finalStatus is not null) evt.FinalStatus = finalStatus;
        return ClaimEventOutboxItem.Create(
            evt.EventId, ClaimFinalizedTopic, claim.Id, JsonSerializer.Serialize(evt, JsonOptions),
            new Dictionary<string, string>
            {
                ["tenant-id"] = tenantId,
                ["event-type"] = evt.EventType,
                ["event-schema-version"] = evt.EventSchemaVersion.ToString(),
                ["event-id"] = evt.EventId,
            },
            _clock.GetUtcNow().UtcDateTime);
    }

    public async Task PublishClaimFinalizedAsync(Claim claim, string tenantId, CancellationToken ct = default)
    {
        if (!_producer.IsConfigured || claim.PendingClaimEvents is not { Count: > 0 }) return;
        try
        {
            using var scope = _scopes.CreateScope();
            var claims = scope.ServiceProvider.GetRequiredService<IClaimRepository>();
            await _drainer.DrainAsync(claims, claim, respectDueTimes: false, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Still pending in the claim document; the dispatcher publishes it.
            _logger.LogError(ex, "Publishing the pending events of claim {ClaimId} failed; the outbox dispatcher retries them", claim.Id);
        }
    }

    public Task PublishWorkNeededAsync(Claim claim, string tenantId, string? actorId, CancellationToken ct = default) =>
        PublishWorkAsync(ClaimWorkEvents.Needed(claim, tenantId, actorId), ct);

    public Task PublishWorkResolvedAsync(Claim claim, string tenantId, string resolution, string? actorId, CancellationToken ct = default) =>
        PublishWorkAsync(ClaimWorkEvents.Resolved(claim, tenantId, resolution, actorId), ct);

    /// <summary>
    /// Same degraded-mode posture as the pended event: never throws.
    /// work-management-service reconciles against claims-service, so a lost
    /// event is repaired rather than leaving a stale work item forever.
    /// </summary>
    private async Task PublishWorkAsync(ClaimWorkEvent evt, CancellationToken ct)
    {
        if (!_producer.IsConfigured)
        {
            _logger.LogDebug("Kafka not configured; skipping {EventType} for claim {ClaimId}", evt.EventType, evt.ClaimId);
            return;
        }

        var headers = new Dictionary<string, string>
        {
            ["tenant-id"] = evt.TenantId,
            ["event-type"] = evt.EventType,
            ["event-version"] = evt.EventVersion,
        };
        try
        {
            await _producer.ProduceAsync(ClaimWorkTopic, evt.ClaimId, JsonSerializer.Serialize(evt, JsonOptions), headers, ct);
            _logger.LogInformation("Published {EventType} for claim {ClaimId} to {Topic}", evt.EventType, evt.ClaimId, ClaimWorkTopic);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogError(ex, "Failed to publish {EventType} for claim {ClaimId}; work-management reconcile repairs it",
                evt.EventType, evt.ClaimId);
        }
    }

    internal static ClaimFinalizedEvent BuildFinalizedEvent(Claim claim, string tenantId)
    {
        var adj = claim.AdjudicationResult;
        var lines = claim.ClaimLines.Select(l => new ClaimFinalizedLineItem
        {
            LineNumber = l.LineNumber,
            BenefitCategory = l.RevenueCode ?? l.PlaceOfServiceCode ?? string.Empty,
            ServiceCode = l.ProcedureCode,
            // Member cost share is the PR group only: a CO/OA entry that
            // happens to reuse CARC 1/2/3 (e.g. a CO-denial) is not
            // deductible/coinsurance/copay the member owes.
            DeductibleApplied = SumPatientResponsibility(l.AdjudicationResult, "1"),
            CoinsuranceApplied = SumPatientResponsibility(l.AdjudicationResult, "2"),
            CopayApplied = SumPatientResponsibility(l.AdjudicationResult, "3"),
            // Engine-computed OOP-eligible amount; excludes cost share the
            // plan keeps outside the OOP max. Claims persisted before the
            // field existed carry null and keep the prior behavior
            // (all patient responsibility counts), which was correct for
            // them since no OOP exclusion could have applied.
            OopApplied = l.AdjudicationResult?.OopAppliedAmount
                ?? l.AdjudicationResult?.PatientResponsibility
                ?? 0m,
            PlanPaid = l.AdjudicationResult?.PaidAmount ?? 0m,
            MemberResponsibility = l.AdjudicationResult?.PatientResponsibility ?? 0m,
            // NAIC full deductible credit as a later payer: the engine
            // credited more deductible than the member owes (PR-1).
            DeductibleCredited = CreditedIfDifferent(
                l.AdjudicationResult?.DeductibleCreditedAmount,
                SumPatientResponsibility(l.AdjudicationResult, "1")),
        }).ToList();

        var status = claim.Status switch
        {
            // External labels are unchanged by the Pay/Deny lifecycle: the
            // adjudicated outcome (Pay / Deny) and its finalized form (Paid /
            // Denied) publish the same label, so consumers (accumulator-
            // service skips "Denied") see one disposition per outcome.
            ClaimStatus.Paid or ClaimStatus.Approved or ClaimStatus.PartiallyPaid => "Paid",
            ClaimStatus.Deny or ClaimStatus.Denied => "Denied",
            ClaimStatus.Voided => "Reversed",
            _ => claim.Status.ToString()
        };

        // Plan-year boundaries: claims-service does not own the plan calendar, so
        // we default to the calendar year containing ServiceDate. benefit-plan-service
        // overrides this downstream when a non-calendar plan year applies. Populating
        // these fields (even as a best-effort default) is what keeps
        // accumulator-service's ResolveSnapshotAsync off the orphan path when no
        // snapshot is pre-seeded; without them every first-ever finalize would be
        // treated as orphaned.
        var serviceDate = claim.ServiceDateFrom;
        var planYearStart = new DateTime(serviceDate.Year, 1, 1);
        var planYearEnd = new DateTime(serviceDate.Year, 12, 31);

        // Family-aggregate determination requires benefit-plan context the claim does
        // not carry. Default false here; accumulator-service applies individual-only
        // unless the producer (or a future plan-enrichment step) sets this flag.
        return new ClaimFinalizedEvent
        {
            TenantId = tenantId,
            ClaimId = claim.Id,
            ClaimNumber = claim.ClaimNumber,
            MemberId = claim.MemberId,
            PlanYearStart = planYearStart,
            PlanYearEnd = planYearEnd,
            ServiceDate = serviceDate,
            AdjudicationTimestamp = claim.AdjudicatedDate ?? DateTimeOffset.UtcNow,
            FinalStatus = status,
            ClaimFrequencyCode = string.IsNullOrWhiteSpace(claim.ClaimFrequencyCode) ? null : claim.ClaimFrequencyCode,
            // The adjustment workflow (5.12) links a replacement version to the
            // version it amends.
            OriginalClaimId = string.IsNullOrWhiteSpace(claim.PredecessorVersionId) ? null : claim.PredecessorVersionId,
            BenefitCategory = claim.PlaceOfServiceCode ?? string.Empty,
            IsFamilyAggregate = false,
            DeductibleApplied = adj?.DeductibleAmount ?? 0m,
            CoinsuranceApplied = adj?.CoinsuranceAmount ?? 0m,
            CopayApplied = adj?.CopayAmount ?? 0m,
            OopApplied = adj?.OopAppliedAmount ?? adj?.PatientResponsibility ?? 0m,
            PlanPaid = adj?.PayerPayment ?? 0m,
            MemberResponsibility = adj?.PatientResponsibility ?? 0m,
            DeductibleCredited = CreditedIfDifferent(adj?.DeductibleCreditedAmount, adj?.DeductibleAmount ?? 0m),
            LineItems = lines
        };
    }

    private static decimal? CreditedIfDifferent(decimal? credited, decimal applied) =>
        credited is { } c && c != applied ? c : null;

    private static decimal SumPatientResponsibility(LineAdjudicationResult? line, string carc) =>
        line?.AdjustmentReasons
            .Where(r => r.GroupCode == "PR" && r.ReasonCode == carc)
            .Sum(r => r.Amount) ?? 0m;

    private static CloudHealthOffice.Events.PendDetails? MapPendDetails(ClaimsService.Models.PendDetails? source)
    {
        if (source is null) return null;
        return new CloudHealthOffice.Events.PendDetails
        {
            PendCode = source.PendCode,
            PendReason = source.PendReason,
            PendedAt = source.PendedAt,
            EditFailures = source.EditFailures
                .Select(e => new CloudHealthOffice.Events.NcciEditFailureSnapshot
                {
                    EditType = e.EditType,
                    RuleId = e.RuleId,
                    Message = e.Message,
                    Column1Code = e.Column1Code,
                    Column2Code = e.Column2Code,
                    AffectedLineNumbers = e.AffectedLineNumbers.ToList(),
                    ModifierOverridePresent = e.ModifierOverridePresent,
                    UnitsBilled = e.UnitsBilled,
                    MueMaxUnits = e.MueMaxUnits,
                    SuggestedCarc = e.SuggestedCarc,
                    SuggestedRarc = e.SuggestedRarc
                })
                .ToList()
        };
    }
}
