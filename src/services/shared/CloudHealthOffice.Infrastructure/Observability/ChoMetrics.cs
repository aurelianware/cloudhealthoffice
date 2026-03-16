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
    /// Dimensions: http.method, http.route, http.status_code, cho.tenant_id.
    /// </summary>
    public static readonly Histogram<double> RequestDuration =
        Meter.CreateHistogram<double>(
            "cho.http.request.duration",
            unit: "s",
            description: "HTTP request duration in seconds");

    /// <summary>
    /// Histogram tracking claim adjudication latency (seconds).
    /// Dimensions: cho.tenant_id, cho.claim_type, cho.adjudication_step.
    /// </summary>
    public static readonly Histogram<double> ClaimProcessingLatency =
        Meter.CreateHistogram<double>(
            "cho.claims.processing.duration",
            unit: "s",
            description: "Claim processing/adjudication latency in seconds");

    /// <summary>
    /// Counter tracking EDI transactions processed.
    /// Dimensions: cho.tenant_id, cho.edi_transaction_type (837, 835, 270, 271, etc.).
    /// </summary>
    public static readonly Counter<long> EdiTransactionCount =
        Meter.CreateCounter<long>(
            "cho.edi.transactions.total",
            unit: "{transaction}",
            description: "Total EDI transactions processed");

    /// <summary>
    /// Counter tracking adjudication outcomes.
    /// Dimensions: cho.tenant_id, cho.outcome (approved, denied, pended).
    /// </summary>
    public static readonly Counter<long> AdjudicationOutcome =
        Meter.CreateCounter<long>(
            "cho.claims.adjudication.outcome.total",
            unit: "{claim}",
            description: "Adjudication outcomes by result type");

    private static string GetAssemblyVersion()
    {
        return typeof(ChoMetrics).Assembly
            .GetName().Version?.ToString() ?? "0.0.0";
    }
}
