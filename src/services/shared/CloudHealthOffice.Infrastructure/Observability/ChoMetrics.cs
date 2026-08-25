using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

namespace CloudHealthOffice.Infrastructure.Observability;

/// <summary>
/// Custom metrics for Cloud Health Office services.
/// All meters share a single "CloudHealthOffice" meter so Prometheus/OTLP
/// exporters only need one subscription.
/// </summary>
public static class ChoMetrics
{
    public const string MeterName = "CloudHealthOffice";

    private static readonly Meter Meter = new(MeterName, GetAssemblyVersion());

    /// <summary>
    /// Histogram tracking end-to-end HTTP request duration (seconds).
    /// Dimensions: http.method, http.route, http.status_code.
    /// </summary>
    public static readonly Histogram<double> RequestDuration =
        Meter.CreateHistogram<double>(
            "cho.http.request.duration",
            unit: "s",
            description: "HTTP request duration in seconds");

    /// <summary>
    /// Histogram tracking claim adjudication latency (seconds).
    /// Dimensions: cho.claim_type, cho.adjudication_step.
    /// </summary>
    public static readonly Histogram<double> ClaimProcessingLatency =
        Meter.CreateHistogram<double>(
            "cho.claims.processing.duration",
            unit: "s",
            description: "Claim processing/adjudication latency in seconds");

    /// <summary>
    /// Counter tracking EDI transactions processed.
    /// Dimensions: cho.edi_transaction_type (837, 835, 270, 271, etc.).
    /// </summary>
    public static readonly Counter<long> EdiTransactionCount =
        Meter.CreateCounter<long>(
            "cho.edi.transactions.total",
            unit: "{transaction}",
            description: "Total EDI transactions processed");

    /// <summary>
    /// Counter tracking adjudication outcomes.
    /// Dimensions: cho.outcome (approved, denied, pended).
    /// </summary>
    public static readonly Counter<long> AdjudicationOutcome =
        Meter.CreateCounter<long>(
            "cho.claims.adjudication.outcome.total",
            unit: "{claim}",
            description: "Adjudication outcomes by result type");

    /// <summary>
    /// Histogram tracking Da Vinci PAS $submit request duration (seconds).
    /// Target: under 15 seconds per PAS IG 2.1.0 Section 5.2.1.
    /// Dimensions: pas.decision, pas.rule.
    /// </summary>
    public static readonly Histogram<double> PasSubmitDuration =
        Meter.CreateHistogram<double>(
            "cho.pas.submit.duration",
            unit: "s",
            description: "Time to process PAS $submit request");

    /// <summary>
    /// Counter tracking PAS $submit decisions by type and rule.
    /// Dimensions: pas.decision (approved, denied, pended, error), pas.rule.
    /// </summary>
    public static readonly Counter<long> PasSubmitDecisions =
        Meter.CreateCounter<long>(
            "cho.pas.submit.decisions.total",
            unit: "{decision}",
            description: "PAS $submit decisions by type");

    /// <summary>
    /// Counter tracking span attributes dropped by the PHI-scrubbing SpanProcessor.
    /// Dimensions: attribute_name, service_name.
    /// </summary>
    public static readonly Counter<long> TelemetryScrubCount =
        Meter.CreateCounter<long>(
            "cho.telemetry.scrub.total",
            unit: "{attribute}",
            description: "Span attributes scrubbed by the PHI SpanProcessor");

    /// <summary>
    /// Counter tracking writes to <c>NetworkParticipation</c> that elide
    /// the panel-gating fields (capability 5.5 soft validation). Drives
    /// the eventual hard-validation cutover — when this counter is zero
    /// across all tenants for a sustained window, the follow-up PR can
    /// flip soft warnings to 400 rejections without breaking callers.
    /// Dimensions: cho.caller (CreateProvider | UpdateProvider |
    /// AddNetworkParticipation), cho.tenant_id.
    /// </summary>
    public static readonly Counter<long> PanelGatingMissingWrites =
        Meter.CreateCounter<long>(
            "cho.provider.panel_gating.missing_writes.total",
            unit: "{write}",
            description: "Writes to NetworkParticipation that elide panel-gating fields (5.5 soft validation)");

    /// <summary>
    /// Counter tracking participations patched by the
    /// <c>NetworkParticipationBackfillService</c> (capability 5.5
    /// admin-triggered backfill). Dimensions: cho.outcome (patched |
    /// skipped | failed | etag_conflict), cho.tenant_id.
    /// </summary>
    public static readonly Counter<long> NetworkParticipationBackfillOutcomes =
        Meter.CreateCounter<long>(
            "cho.provider.network_participation.backfill.outcomes.total",
            unit: "{participation}",
            description: "Participations processed by the panel-gating backfill, by outcome");

    /// <summary>
    /// Counter tracking writes to <c>BenefitPlan.NetworkTiers</c> that
    /// elide <c>NetworkTier.NetworkId</c> (benefit-plan capability 5.5
    /// soft validation). Drives the eventual hard-validation cutover —
    /// when this counter is zero across all tenants for a sustained
    /// window, the follow-up PR can flip soft warnings to 400
    /// rejections without breaking callers. Dimensions:
    /// <c>cho.caller</c> (CreatePlan | UpdatePlan | CreateDraft |
    /// AmendPublished | PublishAndSupersede), <c>cho.tenant_id</c>.
    /// </summary>
    public static readonly Counter<long> NetworkTierMissingNetworkIdWrites =
        Meter.CreateCounter<long>(
            "cho.benefit_plan.network_tier_missing_networkid_writes.total",
            unit: "{write}",
            description: "Writes to BenefitPlan.NetworkTiers that elide NetworkTier.NetworkId (5.5 soft validation)");

    /// <summary>
    /// Counter tracking benefit-plan write rejections by
    /// <c>IPlanLimitValidator</c> (capability 5.7 — ACA §156.130
    /// individual / family OOP cap enforcement). Distinct from soft
    /// validators: every increment corresponds to a 400 rejection.
    /// Dimensions: <c>cho.caller</c> (one of the
    /// <c>PlanLimitWriteCaller</c> values), <c>cho.tenant_id</c>,
    /// <c>cho.reason</c> (PlanYearNotConfigured |
    /// IndividualOopExceedsAcaCap | FamilyOopExceedsAcaCap).
    /// </summary>
    public static readonly Counter<long> PlanLimitValidationFailures =
        Meter.CreateCounter<long>(
            "cho.benefit_plan.plan_limit_validation_failures.total",
            unit: "{rejection}",
            description: "Benefit-plan write rejections from IPlanLimitValidator (5.7 ACA OOP cap enforcement)");

    /// <summary>
    /// Counter tracking network-tier mapping outcomes emitted by the
    /// <c>NetworkTierBackfillService</c> (benefit-plan capability 5.5
    /// admin-triggered backfill). A single benefit plan can contribute
    /// multiple increments when the operator submits multiple tier
    /// mappings against the same plan in one request. Dimensions:
    /// <c>cho.outcome</c> (patched | skipped | not_found | unresolved |
    /// failed), <c>cho.tenant_id</c>.
    /// </summary>
    public static readonly Counter<long> NetworkTierBackfillOutcomes =
        Meter.CreateCounter<long>(
            "cho.benefit_plan.network_tier.backfill.outcomes.total",
            unit: "{mapping}",
            description: "Network-tier mapping outcomes processed by the NetworkId backfill, by outcome");

    /// <summary>
    /// Counter tracking benefits projected by
    /// <c>ChoBenefitPlanProvider.MapToConfig</c> whose <c>Rules</c>
    /// list carried more than one <c>BenefitRulePredicate</c>
    /// (capability BP 5.10). The projection collapses the list to its
    /// first non-null entry — multi-predicate-AND semantics is a
    /// Phase 2 capability. The counter sizes that backlog: when the
    /// counter is non-zero across tenants, multi-predicate authoring
    /// is happening in the wild and Phase 2 design needs to land
    /// before truncation becomes load-bearing. Dimensions:
    /// <c>cho.tenant_id</c>.
    /// </summary>
    public static readonly Counter<long> PredicateMultiRuleTruncated =
        Meter.CreateCounter<long>(
            "cho.benefit_plan.predicate_multi_rule_truncated.total",
            unit: "{benefit}",
            description: "Benefits projected with Rules.Count > 1; only the first predicate is consumed (BP 5.10)");

    /// <summary>
    /// Counter tracking <c>HttpProviderIntegrityGate</c>'s cached-or-live
    /// decision path (capability 5.10). Dimensions: <c>cho.path</c>
    /// (<c>cached_hit</c> | <c>stale_fallback</c> | <c>null_fallback</c>
    /// | <c>live_only</c>) and <c>cho.rating</c> (the resolved rating
    /// label, or <c>"unknown"</c>).
    ///
    /// <para>
    /// The metric drives operational tuning of
    /// <c>ProviderIntegrityGate:StalenessFallbackThreshold</c>: high
    /// <c>stale_fallback</c> rates suggest the threshold is tighter than
    /// the verification cadence; high <c>null_fallback</c> rates suggest
    /// the projection backfill hasn't been run on that tenant. See
    /// <c>docs/architecture/integrity-score-consumption.md</c>.
    /// </para>
    /// </summary>
    public static readonly Counter<long> ProviderIntegrityGateDecisions =
        Meter.CreateCounter<long>(
            "cho.provider.integrity_gate.decisions.total",
            unit: "{decision}",
            description: "HttpProviderIntegrityGate cached-or-live decisions by path and rating");

    /// <summary>
    /// Per-tenant snapshot of providers whose <c>LastVerifiedAt</c> is
    /// older than <c>IntegrityProjection:StalenessAlertThreshold</c>
    /// (capability 5.10). Updated by
    /// <c>IntegrityProjectionStalenessReporter</c> on each worker sweep
    /// cycle; read by the observable gauge below on each scrape.
    /// </summary>
    private static readonly ConcurrentDictionary<string, long> IntegrityScoreStaleCounts
        = new(StringComparer.Ordinal);

    /// <summary>
    /// Observable gauge surfacing the per-tenant stale-score snapshot
    /// (capability 5.10). Dimension: <c>cho.tenant_id</c>.
    ///
    /// <para>
    /// Underscored snake_case metric name + <c>cho.*</c> tag keeps this
    /// instrument aligned with sibling provider-service metrics in
    /// <see cref="PanelGatingMissingWrites"/> and
    /// <see cref="NetworkParticipationBackfillOutcomes"/>.
    /// </para>
    /// </summary>
    public static readonly ObservableGauge<long> ProviderIntegrityScoreStaleCount =
        Meter.CreateObservableGauge<long>(
            "cho.provider.integrity_score.stale_count",
            observeValues: () =>
            {
                var snapshot = IntegrityScoreStaleCounts.ToArray();
                var measurements = new Measurement<long>[snapshot.Length];
                for (var i = 0; i < snapshot.Length; i++)
                {
                    measurements[i] = new Measurement<long>(
                        snapshot[i].Value,
                        new KeyValuePair<string, object?>("cho.tenant_id", snapshot[i].Key));
                }
                return measurements;
            },
            unit: "{provider}",
            description: "Providers whose cached integrity score is stale, per tenant");

    /// <summary>
    /// Update the per-tenant stale-score snapshot read by
    /// <see cref="ProviderIntegrityScoreStaleCount"/>. Called from
    /// <c>IntegrityProjectionStalenessReporter</c> after each worker
    /// sweep cycle. Setting <paramref name="count"/> to a negative value
    /// removes the entry (used when the threshold is disabled).
    /// </summary>
    public static void SetIntegrityScoreStaleCount(string tenantId, long count)
    {
        if (string.IsNullOrEmpty(tenantId)) return;
        if (count < 0) IntegrityScoreStaleCounts.TryRemove(tenantId, out _);
        else IntegrityScoreStaleCounts[tenantId] = count;
    }

    /// <summary>
    /// Test hook — clears the stale-count snapshot. Production code
    /// should never call this; tests use it to reset state between
    /// runs. Public (rather than internal) so consumers in test
    /// projects across assembly boundaries can reset the static
    /// snapshot without managing <c>InternalsVisibleTo</c> entries.
    /// </summary>
    public static void ResetIntegrityScoreStaleCounts() => IntegrityScoreStaleCounts.Clear();

    /// <summary>
    /// Counter tracking claims-service Cosmos partition-key migration
    /// runs (capability 5.1b). One increment per
    /// <c>POST /api/v1/admin/claims/cosmos-migration/run</c>
    /// invocation. Dimensions: <c>cho.outcome</c> (success | partial |
    /// failed), <c>cho.dry_run</c> (true | false).
    /// </summary>
    public static readonly Counter<long> ClaimsCosmosMigrationRuns =
        Meter.CreateCounter<long>(
            "cho.claims.cosmos_migration.runs.total",
            unit: "{run}",
            description: "Cosmos partition-key migration runs by outcome (5.1b)");

    /// <summary>
    /// Counter tracking individual document outcomes within a Cosmos
    /// partition-key migration run (capability 5.1b). Dimensions:
    /// <c>cho.outcome</c> (written | would_write | skipped | errored).
    /// </summary>
    public static readonly Counter<long> ClaimsCosmosMigrationDocuments =
        Meter.CreateCounter<long>(
            "cho.claims.cosmos_migration.documents.total",
            unit: "{document}",
            description: "Documents processed per migration run, by outcome (5.1b)");

    /// <summary>
    /// Histogram tracking Cosmos partition-key migration duration
    /// (capability 5.1b). Dimensions: <c>cho.outcome</c>,
    /// <c>cho.dry_run</c>.
    /// </summary>
    public static readonly Histogram<double> ClaimsCosmosMigrationDuration =
        Meter.CreateHistogram<double>(
            "cho.claims.cosmos_migration.duration",
            unit: "s",
            description: "Cosmos partition-key migration run duration in seconds (5.1b)");

    /// <summary>
    /// Histogram tracking payer-directory synchronization duration (seconds).
    /// Dimensions: cho.outcome (success | failed).
    /// </summary>
    public static readonly Histogram<double> PayerSyncDuration =
        Meter.CreateHistogram<double>(
            "cho.payer_reference.sync.duration",
            unit: "s",
            description: "Payer directory synchronization duration in seconds");

    /// <summary>
    /// Counter tracking payer-directory record outcomes.
    /// Dimensions: cho.outcome (received | added | updated | disabled).
    /// </summary>
    public static readonly Counter<long> PayerSyncRecords =
        Meter.CreateCounter<long>(
            "cho.payer_reference.sync.records.total",
            unit: "{record}",
            description: "Payer directory records processed during synchronization");

    /// <summary>
    /// Counter tracking payer-directory sync failures.
    /// Dimensions: cho.category.
    /// </summary>
    public static readonly Counter<long> PayerSyncFailures =
        Meter.CreateCounter<long>(
            "cho.payer_reference.sync.failures.total",
            unit: "{failure}",
            description: "Payer directory synchronization failures");

    /// <summary>
    /// Counter tracking payer resolution outcomes.
    /// Dimensions: cho.result (success | not_found | ambiguous | missing_identifier |
    /// unsupported | enrollment_required | disabled | unavailable).
    /// </summary>
    public static readonly Counter<long> PayerResolutionTotal =
        Meter.CreateCounter<long>(
            "cho.payer_reference.resolution.total",
            unit: "{resolution}",
            description: "Canonical payer resolution outcomes");

    /// <summary>
    /// Counter tracking inbound payer-side eligibility inquiries.
    /// Dimensions: cho.adapter, cho.business_status, cho.coverage_status,
    /// cho.transport_status. Never labeled with member identity.
    /// </summary>
    public static readonly Counter<long> PayerEligibilityInquiries =
        Meter.CreateCounter<long>(
            "cho.payer_eligibility.inquiries.total",
            unit: "{inquiry}",
            description: "Inbound payer-side eligibility inquiries by business and coverage outcome");

    /// <summary>
    /// Histogram tracking inbound payer-side eligibility latency (seconds).
    /// Dimensions: cho.adapter, cho.business_status.
    /// </summary>
    public static readonly Histogram<double> PayerEligibilityDuration =
        Meter.CreateHistogram<double>(
            "cho.payer_eligibility.duration",
            unit: "s",
            description: "Inbound payer-side eligibility inquiry duration in seconds");

    /// <summary>
    /// Counter tracking outbound claim transmissions.
    /// Dimensions: cho.gateway, cho.claim_type, cho.status, cho.error_category.
    /// Never labeled with member/payer/provider identity.
    /// </summary>
    public static readonly Counter<long> ClaimSubmissions =
        Meter.CreateCounter<long>(
            "cho.claim_submission.transmissions.total",
            unit: "{claim}",
            description: "Outbound claim transmissions by gateway, claim type, and status");

    /// <summary>
    /// Histogram tracking outbound claim submission latency (seconds).
    /// Dimensions: cho.gateway, cho.claim_type.
    /// </summary>
    public static readonly Histogram<double> ClaimSubmissionDuration =
        Meter.CreateHistogram<double>(
            "cho.claim_submission.duration",
            unit: "s",
            description: "Outbound claim submission duration in seconds");

    /// <summary>
    /// Counter tracking 277CA acknowledgment processing outcomes.
    /// Dimensions: cho.status.
    /// </summary>
    public static readonly Counter<long> ClaimAcknowledgments =
        Meter.CreateCounter<long>(
            "cho.claim_acknowledgment.processed.total",
            unit: "{acknowledgment}",
            description: "Inbound 277CA acknowledgments by canonical status");

    /// <summary>
    /// Histogram tracking 277CA acknowledgment processing duration (seconds).
    /// Dimensions: cho.status.
    /// </summary>
    public static readonly Histogram<double> ClaimAcknowledgmentDuration =
        Meter.CreateHistogram<double>(
            "cho.claim_acknowledgment.duration",
            unit: "s",
            description: "Inbound 277CA acknowledgment processing duration in seconds");

    /// <summary>
    /// Counter tracking outbound 275 attachment transmissions.
    /// Dimensions: cho.gateway, cho.status, cho.error_category.
    /// Never labeled with member identity or file contents.
    /// </summary>
    public static readonly Counter<long> ClaimAttachments =
        Meter.CreateCounter<long>(
            "cho.claim_attachment.transmissions.total",
            unit: "{attachment}",
            description: "Outbound claim attachment transmissions by gateway and status");

    /// <summary>
    /// Histogram tracking outbound 275 attachment latency (seconds).
    /// Dimensions: cho.gateway.
    /// </summary>
    public static readonly Histogram<double> ClaimAttachmentDuration =
        Meter.CreateHistogram<double>(
            "cho.claim_attachment.duration",
            unit: "s",
            description: "Outbound claim attachment submission duration in seconds");

    /// <summary>
    /// Counter tracking inbound payer-side 275 receipts.
    /// Dimensions: cho.adapter, cho.status, cho.error_category, cho.association.
    /// </summary>
    public static readonly Counter<long> InboundClaimAttachments =
        Meter.CreateCounter<long>(
            "cho.inbound_claim_attachment.received.total",
            unit: "{attachment}",
            description: "Inbound payer-side claim attachments by adapter and status");

    public static readonly Histogram<double> InboundClaimAttachmentDuration =
        Meter.CreateHistogram<double>(
            "cho.inbound_claim_attachment.duration",
            unit: "s",
            description: "Inbound payer-side claim attachment processing duration in seconds");

    /// <summary>
    /// Counter tracking outbound 276/277 claim-status inquiries.
    /// Dimensions: cho.gateway, cho.status, cho.error_category.
    /// Never labeled with member or provider identity.
    /// </summary>
    public static readonly Counter<long> ClaimStatusInquiries =
        Meter.CreateCounter<long>(
            "cho.claim_status.inquiries.total",
            unit: "{inquiry}",
            description: "Outbound 276/277 claim status inquiries by gateway and normalized status");

    /// <summary>
    /// Histogram tracking outbound 276/277 claim-status latency (seconds).
    /// Dimensions: cho.gateway, cho.status.
    /// </summary>
    public static readonly Histogram<double> ClaimStatusDuration =
        Meter.CreateHistogram<double>(
            "cho.claim_status.duration",
            unit: "s",
            description: "Outbound 276/277 claim status inquiry duration in seconds");

    /// <summary>
    /// Counter tracking inbound 835 remittance receipts.
    /// Dimensions: cho.gateway, cho.status, cho.error_category.
    /// </summary>
    public static readonly Counter<long> Remittances =
        Meter.CreateCounter<long>(
            "cho.remittance.received.total",
            unit: "{remittance}",
            description: "Inbound 835 remittances by gateway and lifecycle status");

    public static readonly Histogram<double> RemittanceDuration =
        Meter.CreateHistogram<double>(
            "cho.remittance.duration",
            unit: "s",
            description: "Inbound 835 remittance processing duration in seconds");

    public static readonly Counter<long> RemittedClaims =
        Meter.CreateCounter<long>(
            "cho.remittance.claims.total",
            unit: "{claim}",
            description: "Claims included on inbound 835 remittances by match outcome");

    /// <summary>
    /// Counter tracking 835 remittances posted to claim financials and accumulators.
    /// Dimensions: cho.gateway, cho.status. Never labeled with check/trace numbers.
    /// </summary>
    public static readonly Counter<long> RemittancePosted =
        Meter.CreateCounter<long>(
            "cho.remittance.posted.total",
            unit: "{remittance}",
            description: "Inbound 835 remittances posted to claims and accumulators");

    /// <summary>
    /// Counter tracking claim intelligence views generated.
    /// Dimensions: cho.status, cho.next_action. Never labeled with PHI.
    /// </summary>
    public static readonly Counter<long> ClaimIntelligenceViews =
        Meter.CreateCounter<long>(
            "cho.claim_intelligence.views.total",
            unit: "{view}",
            description: "Claim intelligence views composed by lifecycle status");

    public static readonly Histogram<double> ClaimIntelligenceDuration =
        Meter.CreateHistogram<double>(
            "cho.claim_intelligence.duration",
            unit: "s",
            description: "Claim intelligence composition duration in seconds");

    public static readonly Counter<long> ClaimIntelligenceRebuilds =
        Meter.CreateCounter<long>(
            "cho.claim_intelligence.rebuilds.total",
            unit: "{rebuild}",
            description: "Claim intelligence projections rebuilt from transaction stores");

    public static readonly Counter<long> ClaimIntelligenceFailures =
        Meter.CreateCounter<long>(
            "cho.claim_intelligence.failed.total",
            unit: "{failure}",
            description: "Failed claim intelligence projections");

    public static readonly Counter<long> ClaimIntelligenceMissingLinks =
        Meter.CreateCounter<long>(
            "cho.claim_intelligence.missing_links.total",
            unit: "{link}",
            description: "Missing transaction links observed while composing claim intelligence");

    private static string GetAssemblyVersion()
    {
        return typeof(ChoMetrics).Assembly
            .GetName().Version?.ToString() ?? "0.0.0";
    }
}
