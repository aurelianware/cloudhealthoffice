using System.Diagnostics.Metrics;

namespace CloudHealthOffice.BenefitEngine.Services;

/// <summary>
/// Engine-internal counters that don't depend on
/// <c>CloudHealthOffice.Infrastructure</c> (the engine project sits
/// below the shared infra assembly). All instruments share the
/// canonical <c>"CloudHealthOffice"</c> meter name so OpenTelemetry /
/// Prometheus exporters need only one subscription regardless of
/// which assembly defines the instrument.
/// </summary>
internal static class BenefitEngineMetrics
{
    private const string MeterName = "CloudHealthOffice";

    private static readonly Meter Meter = new(MeterName,
        typeof(BenefitEngineMetrics).Assembly.GetName().Version?.ToString() ?? "0.0.0");

    /// <summary>
    /// Counter incremented when <see cref="ServiceCategoryResolver"/>
    /// drops one or more rows from a non-empty mapping set because
    /// they fell outside the claim's effective window or carry
    /// <c>IsActive == false</c>. Operators read this to confirm
    /// time-bounded mapping authoring is producing real filtering
    /// activity. Dimensions: <c>cho.tenant_id</c>, <c>cho.scope</c>
    /// (<c>plan</c> | <c>tenant</c>).
    /// </summary>
    public static readonly Counter<long> ScmFilteredByEffectiveWindow =
        Meter.CreateCounter<long>(
            "cho.benefit_plan.scm_filtered_by_effective_window.total",
            unit: "{filter}",
            description: "Service-category mappings dropped by effective-window/IsActive filtering (BP 5.10)");

    /// <summary>
    /// Counter incremented when <c>BenefitRuleGate</c> meets a
    /// candidate benefit carrying a non-null
    /// <see cref="Domain.BenefitRulePredicate"/> but the request did
    /// not supply <c>MemberContext</c>. Drives the per-tenant signal
    /// "this plan has rules but my caller isn't supplying context".
    /// Dimensions: <c>cho.tenant_id</c>,
    /// <c>cho.service_type_code</c>.
    /// </summary>
    public static readonly Counter<long> PredicateSkippedNoMemberContext =
        Meter.CreateCounter<long>(
            "cho.benefit_plan.predicate_skipped_no_member_context.total",
            unit: "{skip}",
            description: "Benefits with predicates encountered without a MemberContext (BP 5.10)");
}
