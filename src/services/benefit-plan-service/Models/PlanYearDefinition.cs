using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace BenefitPlanService.Models;

/// <summary>
/// Declarative description of a plan's renewal cycle (5.3 — Plan-Year
/// Definition). Until now <see cref="BenefitPlan.EffectiveDate"/> and
/// <see cref="BenefitPlan.TerminationDate"/> were the only signals about
/// when a plan year started or ended; downstream consumers had to infer
/// the type (calendar / contract / fiscal / anniversary) from naming
/// conventions or out-of-band config. This makes the type a first-class
/// declarative field so the scheduler, accumulator-service, and benefit
/// engine all derive the same window deterministically.
///
/// <para>
/// Backward compatibility: <see cref="BenefitPlan.EffectiveDate"/> and
/// <see cref="BenefitPlan.TerminationDate"/> are preserved verbatim and
/// remain authoritative for plan activation. PlanYearDefinition is
/// optional — plans created before this feature deserialize with a null
/// definition, which the scheduler treats as opt-out.
/// </para>
/// </summary>
public class PlanYearDefinition
{
    /// <summary>
    /// Anchor date for the current plan year. For
    /// <see cref="PlanYearType.CalendarYear"/> this is January 1; for
    /// <see cref="PlanYearType.ContractYear"/> and
    /// <see cref="PlanYearType.FiscalYear"/> it is the contract / fiscal
    /// start; for <see cref="PlanYearType.EnrollmentAnniversary"/> it is
    /// the member's enrollment effective date.
    /// </summary>
    [Required]
    [JsonPropertyName("planYearStart")]
    public DateTime PlanYearStart { get; set; }

    /// <summary>
    /// Inclusive end of the current plan year. Computed by the publisher
    /// when <see cref="ComputeWindow"/> runs, but persisted here so the
    /// authoritative value travels with the plan and downstream consumers
    /// never have to recompute.
    /// </summary>
    [Required]
    [JsonPropertyName("planYearEnd")]
    public DateTime PlanYearEnd { get; set; }

    [Required]
    [JsonPropertyName("planYearType")]
    public PlanYearType PlanYearType { get; set; } = PlanYearType.CalendarYear;

    /// <summary>
    /// Number of days after <see cref="PlanYearEnd"/> during which a
    /// claim with a service date in the closing year may still be
    /// applied to that year's accumulator. 0 disables carryover (default).
    /// </summary>
    [JsonPropertyName("carryoverDays")]
    public int CarryoverDays { get; set; }

    /// <summary>
    /// Day-of-year on which non-anniversary plans reset. Matches
    /// <see cref="PlanYearStart"/> for calendar / contract / fiscal
    /// plans; ignored for anniversary plans where each member rolls on
    /// their own enrollment date. Persisted explicitly so the scheduler
    /// does not need to re-derive from the start date on every tick.
    /// </summary>
    [JsonPropertyName("annualResetDay")]
    public int? AnnualResetDay { get; set; }

    /// <summary>
    /// Computes the plan-year window containing <paramref name="asOf"/>.
    /// Returns inclusive start and end dates.
    ///
    /// <para>
    /// For calendar plans the window snaps to Jan 1 – Dec 31 of
    /// <paramref name="asOf"/>'s year. For contract / fiscal /
    /// anniversary plans both <see cref="PlanYearStart"/> and
    /// <see cref="PlanYearEnd"/> roll forward (or backward) in
    /// 1-year hops until the window contains <paramref name="asOf"/>.
    /// Honoring the persisted end keeps day-of-year boundaries
    /// authoritative — leap years and explicit terminus dates survive
    /// the rollover without drift.
    /// </para>
    /// </summary>
    public (DateTime Start, DateTime End) ComputeWindow(DateTime asOf)
    {
        if (PlanYearType == PlanYearType.CalendarYear)
        {
            // Calendar plans always reset Jan 1, regardless of the
            // persisted anchor's year.
            var calStart = new DateTime(asOf.Year, 1, 1);
            return (calStart, calStart.AddYears(1).AddDays(-1));
        }

        // Contract / Fiscal / Anniversary: roll the persisted
        // (start, end) pair forward together so the persisted end's
        // day-of-year is preserved across rollovers. Caps the loop at
        // 200 iterations to defend against pathological inputs.
        var start = PlanYearStart.Date;
        var end = PlanYearEnd.Date;
        // Defensive: if end was never set or is malformed, derive the
        // canonical 1-year-minus-a-day window from start.
        if (end < start) end = start.AddYears(1).AddDays(-1);

        var safety = 0;
        while (asOf > end && safety++ < 200)
        {
            start = start.AddYears(1);
            end = end.AddYears(1);
        }
        while (asOf < start && safety++ < 200)
        {
            start = start.AddYears(-1);
            end = end.AddYears(-1);
        }
        return (start, end);
    }
}

/// <summary>
/// Enumerates the four plan-year shapes the platform supports. Used by
/// <see cref="PlanYearDefinition.ComputeWindow"/> and by the scheduler
/// to decide when to emit transition events.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PlanYearType
{
    /// <summary>January 1 – December 31. The most common shape.</summary>
    CalendarYear = 1,

    /// <summary>
    /// Anchored to a contract effective date. Common for self-funded
    /// employer plans. Resets on the contract anniversary.
    /// </summary>
    ContractYear = 2,

    /// <summary>
    /// Anchored to the payer's fiscal year (often July 1 or October 1).
    /// </summary>
    FiscalYear = 3,

    /// <summary>
    /// Each member rolls on their own enrollment effective date.
    /// Common for individual market and Medicare Advantage SEPs.
    /// </summary>
    EnrollmentAnniversary = 4
}

/// <summary>
/// Plan-level accumulator declaration. Lets the plan author tell the
/// accumulator-service which counters to maintain and how each one
/// behaves at plan-year boundaries. The accumulator-service is the
/// runtime owner of the actual numbers; this type is authoring metadata
/// only.
///
/// <para>
/// A plan may declare zero or more <see cref="AccumulatorTarget"/>s.
/// When the scheduler emits a <see cref="PlanYearTransitionEvent"/>, the
/// downstream subscriber inspects each target's
/// <see cref="ResetBehavior"/> to decide whether to zero, roll over, or
/// ignore the corresponding accumulator. The subscriber must remain
/// idempotent — see docs/architecture/plan-year-definition.md.
/// </para>
/// </summary>
public class AccumulatorTarget
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Benefit category this counter is keyed on (free-form; matches
    /// <c>ServiceAccumulator.BenefitCategory</c> in accumulator-service).
    /// "Deductible" / "OOP" are reserved for the cost-share rollups.
    /// </summary>
    [Required]
    [JsonPropertyName("benefitCategory")]
    public string BenefitCategory { get; set; } = string.Empty;

    /// <summary>USD | Visits | Days | Units. Matches accumulator-service.</summary>
    [JsonPropertyName("unit")]
    public string Unit { get; set; } = "USD";

    [JsonPropertyName("limit")]
    public decimal Limit { get; set; }

    [Required]
    [JsonPropertyName("resetBehavior")]
    public PlanYearResetBehavior ResetBehavior { get; set; } = PlanYearResetBehavior.ResetAtPlanYearEnd;

    /// <summary>
    /// Maximum amount that may carry over when
    /// <see cref="ResetBehavior"/> is <see cref="PlanYearResetBehavior.RolloverWithCap"/>.
    /// Ignored for the other behaviors.
    /// </summary>
    [JsonPropertyName("rolloverCap")]
    public decimal? RolloverCap { get; set; }
}

/// <summary>
/// How an <see cref="AccumulatorTarget"/> behaves when the plan year
/// rolls over. Consumed by the accumulator-service when it processes a
/// <see cref="PlanYearTransitionEvent"/>.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PlanYearResetBehavior
{
    /// <summary>Zero the counter at <see cref="PlanYearDefinition.PlanYearEnd"/>.</summary>
    ResetAtPlanYearEnd = 1,

    /// <summary>Carry the counter across boundaries unchanged (e.g. lifetime maximums).</summary>
    NoReset = 2,

    /// <summary>
    /// Carry up to <see cref="AccumulatorTarget.RolloverCap"/> into the
    /// new year; discard the remainder. Common for HSA-style funds.
    /// </summary>
    RolloverWithCap = 3,

    /// <summary>
    /// New plan starts at the closing balance of the predecessor plan.
    /// Used when a plan replaces another mid-stream (mergers, plan
    /// switches) — the accumulator-service resolves the predecessor via
    /// the version chain.
    /// </summary>
    InheritFromPredecessorPlan = 4
}
