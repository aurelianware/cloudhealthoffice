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

    private static string GetAssemblyVersion()
    {
        return typeof(ChoMetrics).Assembly
            .GetName().Version?.ToString() ?? "0.0.0";
    }
}
