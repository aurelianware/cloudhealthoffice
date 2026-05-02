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

    private static string GetAssemblyVersion()
    {
        return typeof(ChoMetrics).Assembly
            .GetName().Version?.ToString() ?? "0.0.0";
    }
}
