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
    /// Dimensions: caller (CreateProvider | UpdateProvider |
    /// AddNetworkParticipation), tenant_id.
    /// </summary>
    public static readonly Counter<long> PanelGatingMissingWrites =
        Meter.CreateCounter<long>(
            "provider_service_panel_gating_missing_writes_total",
            unit: "{write}",
            description: "Writes to NetworkParticipation that elide panel-gating fields (5.5 soft validation)");

    /// <summary>
    /// Counter tracking participations patched by the
    /// <c>NetworkParticipationBackfillService</c> (capability 5.5
    /// admin-triggered backfill). Dimensions: outcome (patched | skipped
    /// | failed | etag_conflict), tenant_id.
    /// </summary>
    public static readonly Counter<long> NetworkParticipationBackfillOutcomes =
        Meter.CreateCounter<long>(
            "provider_service_network_participation_backfill_outcomes_total",
            unit: "{participation}",
            description: "Participations processed by the panel-gating backfill, by outcome");

    private static string GetAssemblyVersion()
    {
        return typeof(ChoMetrics).Assembly
            .GetName().Version?.ToString() ?? "0.0.0";
    }
}
