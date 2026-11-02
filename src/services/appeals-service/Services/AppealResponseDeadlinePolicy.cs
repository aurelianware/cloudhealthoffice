using AppealsService.Models;

namespace AppealsService.Services;

/// <summary>
/// Pure regulatory response-deadline policy for appeals. No side effects;
/// no clock access — callers pass the receipt instant.
///
/// Three distinct answers:
///   - <b>Default target</b> (<see cref="MaxResponseWindow(AppealCoverage, AppealType, AppealLevel, bool)"/> /
///     <see cref="ComputeTargetResponseDate(DateTime, AppealCoverage, AppealType, AppealLevel, bool)"/>):
///     the conservative clock applied when the caller supplies no
///     <see cref="Appeal.TargetResponseDate"/>.
///   - <b>Enforceable maximum</b> (<see cref="EnforceableMaximumWindow(AppealCoverage, AppealType, AppealLevel, bool)"/> /
///     <see cref="ComputeEnforceableMaximum(DateTime, AppealCoverage, AppealType, AppealLevel, bool, IHolidayCalendar?)"/>):
///     a genuine federal ceiling. A caller-supplied date later than this is
///     rejected. <c>null</c> means no federal ceiling exists and caller
///     dates are not rejected.
///   - <b>Extension</b> (<see cref="GetExtensionRule"/> /
///     <see cref="ComputeMaxExtendedTargetResponseDate"/>): the one-time
///     extension the plan may take, bounded by the enforceable maximum plus
///     the permitted extension.
///
/// Internal (plan-level) clocks:
///                                       Default target   Enforceable max
///   Appeal, expedited (every LOB)       72 hours         72 hours
///     42 CFR 422.590(e); 423.590(d); 438.408(b)(3);
///     29 CFR 2560.503-1(i)(2)(i); 45 CFR 147.136(b)
///   Appeal, standard                    30 days          30 days
///     42 CFR 422.590(a) (MA pre-service); 438.408(b)(2);
///     29 CFR 2560.503-1(i)(2)(ii) (pre-service)
///   Part D redetermination, standard     7 days           7 days   (42 CFR 423.590(a))
///   Grievance, MA / Part D standard     30 days          30 days  (42 CFR 422.564(e), 423.564(e))
///   Grievance, MA / Part D urgent       24 hours         30 days
///     (422.564(f) / 423.564(f)'s 24h applies only to grievances about an
///     extension or a refusal to expedite, which the model cannot tell
///     apart — so 24h is a default only and the standard-grievance ceiling
///     is enforced)
///   Grievance, Medicaid standard        90 days          90 days  (42 CFR 438.408(b)(1))
///   Grievance, Medicaid urgent          72 hours         90 days  (no expedited grievance clock)
///   Grievance, Commercial/Exchange   30d / 72h        none     (state law; no federal clock)
///
/// External review (<see cref="AppealLevel.ExternalReview"/> or
/// <see cref="AppealType.ExternalReview"/>):
///                                       Default target   Enforceable max
///   MA IRE reconsideration              30d / 72h        30d / 72h  (42 CFR 422.592; IRE applies
///                                                                    the 422.590 clocks)
///   Part D IRE reconsideration           7d / 72h         7d / 72h  (42 CFR 423.600)
///   Commercial / Exchange (ACA)      45d / 72h        45d / 72h  (45 CFR 147.136(d)(2)-(3);
///     external review                                                29 CFR 2590.715-2719(d))
///   Medicaid State Fair Hearing         90d / 72h        90d / 3 working days  (42 CFR 431.244(f))
///     (expedited: 72h is the default; the enforceable instant is receipt
///     + 3 working days, skipping Saturdays, Sundays and holidays — see
///     <see cref="AddWorkingDays"/>. Holidays come from an
///     <see cref="IHolidayCalendar"/>: U.S. federal holidays by default,
///     plus per-tenant / per-state additions from configuration via
///     <see cref="IAppealHolidayCalendarProvider"/>)
///
/// Extensions (one per appeal, see <see cref="GetExtensionRule"/>):
///   Medicare Advantage appeals                 up to 14 days, standard AND expedited (42 CFR 422.590(f))
///   Medicare Advantage / Part D grievances     up to 14 days, standard only (42 CFR 422.564(e)(2),
///                                              423.564(e)(2)); none for grievances the plan flagged
///                                              urgent (treated as expedited, 422.564(f) / 423.564(f))
///   Medicaid managed care appeals/grievances   up to 14 days (42 CFR 438.408(c))
///   Medicare Part D redeterminations           none (42 CFR 423.590 has no extension provision)
///   Commercial / Exchange                   none — 29 CFR 2560.503-1(i) does not permit a plan to
///                                              unilaterally extend appeal decision periods
///                                              (narrow multiemployer-board exception not modeled)
///   Any external review / State Fair Hearing   none — the IRE / external reviewer / State controls
///                                              that clock, not the plan
/// </summary>
/// <remarks>
/// Conservative ceilings: the appeal model does not distinguish pre-service
/// from post-service / payment appeals, so the shorter pre-service clock is
/// both the default and the enforced ceiling for every standard appeal even
/// though MA payment reconsiderations (60 days, 42 CFR 422.590(b)), Part D
/// payment redeterminations (14 days, 42 CFR 423.590(b)) and commercial
/// post-service appeals (29 CFR 2560.503-1(i)(2)(iii)) allow longer.
/// Likewise the model has no Part B drug discriminator, so MA extensions
/// are allowed even though 42 CFR 422.590(f) excludes Part B drug requests;
/// callers handling a Part B drug request must not invoke the extension
/// operation. <see cref="LineOfBusiness.Medicare"/> is Medicare Advantage
/// (Part C); Medicare with <see cref="AppealBenefitType.PartDDrug"/>
/// (<see cref="AppealCoverage.PartD"/>) covers Part D (including MA-PD
/// drug coverage). TRICARE and VA have no rules here; intake refuses them
/// and they fall to the commercial clocks if ever asked.
///
/// MA IRE auto-forward: an MA plan that affirms an adverse reconsideration,
/// in whole or part, must auto-forward the case file to the IRE
/// (42 CFR 422.590(a)(2), (e)(5); 422.592). That hand-off integration is
/// NOT built here — the external-review clock only governs the deadline
/// tracked on an appeal recorded at <see cref="AppealLevel.ExternalReview"/>.
/// Part D does not auto-forward except when the plan misses its
/// redetermination clock (42 CFR 423.590(c), (e)).
///
/// Medicaid State Fair Hearing: the State, not the plan, controls the
/// hearing clock. 42 CFR 431.244(f)(1)(ii) runs the 90 days from the
/// enrollee's MCO appeal filing excluding the time taken to request the
/// hearing; this policy measures from the hearing request's receipt, so
/// the tracked date is informational.
/// </remarks>
public static class AppealResponseDeadlinePolicy
{
    /// <summary>The federal regime whose clocks apply.</summary>
    private enum ClockLine { Other, MedicareAdvantage, PartD, Medicaid }

    private static ClockLine Clock(AppealCoverage coverage) => coverage switch
    {
        { IsPartD: true } => ClockLine.PartD,
        { LineOfBusiness: LineOfBusiness.Medicare } => ClockLine.MedicareAdvantage,
        { LineOfBusiness: LineOfBusiness.Medicaid } => ClockLine.Medicaid,
        _ => ClockLine.Other, // Commercial and Exchange (ACA / ERISA rules)
    };

    public static readonly TimeSpan Expedited = TimeSpan.FromHours(72);
    public static readonly TimeSpan StandardAppeal = TimeSpan.FromDays(30);
    public static readonly TimeSpan MedicareStandardGrievance = TimeSpan.FromDays(30);
    public static readonly TimeSpan MedicaidStandardGrievance = TimeSpan.FromDays(90);
    public static readonly TimeSpan PartDStandardRedetermination = TimeSpan.FromDays(7);
    public static readonly TimeSpan AcaExternalReviewStandard = TimeSpan.FromDays(45);
    public static readonly TimeSpan StateFairHearingStandard = TimeSpan.FromDays(90);

    /// <summary>
    /// Working days an expedited State Fair Hearing has (42 CFR 431.244(f)(2)).
    /// </summary>
    public const int StateFairHearingExpeditedWorkingDays = 3;

    /// <summary>
    /// Widest calendar span <see cref="StateFairHearingExpeditedWorkingDays"/>
    /// can cover under the U.S. federal holiday calendar: a receipt the day
    /// before a holiday that touches a weekend runs six days (Friday before
    /// a Monday holiday → Thursday; Wednesday before Thanksgiving → the
    /// following Tuesday). Tenant-added holidays can widen it further, so
    /// this is informational only. Only the window-based
    /// <see cref="EnforceableMaximumWindow(AppealCoverage, AppealType, AppealLevel, bool)"/>
    /// reports this upper bound; the instant-based
    /// <see cref="ComputeEnforceableMaximum(DateTime, AppealCoverage, AppealType, AppealLevel, bool, IHolidayCalendar?)"/>
    /// applies exact working-day arithmetic from the receipt.
    /// </summary>
    public static readonly TimeSpan StateFairHearingExpeditedCeiling = TimeSpan.FromDays(6);

    public static readonly TimeSpan MaxExtension = TimeSpan.FromDays(14);

    /// <summary>
    /// True when the appeal is at the external / independent review tier.
    /// Either the level or the type marks it — a FHIR submission may carry
    /// only one of the two. A grievance is never external review: grievance
    /// clocks win (grievances are not appealable to an IRE).
    /// </summary>
    public static bool IsExternalReview(AppealType appealType, AppealLevel appealLevel) =>
        appealType != AppealType.Grievance
        && (appealType == AppealType.ExternalReview || appealLevel == AppealLevel.ExternalReview);

    /// <summary>
    /// False when the decision clock belongs to someone other than the
    /// plan (Medicaid State Fair Hearing). The tracked deadline is then
    /// informational and the plan cannot extend it.
    /// </summary>
    public static bool IsPlanControlled(AppealCoverage coverage, AppealType appealType, AppealLevel appealLevel) =>
        !(Clock(coverage) == ClockLine.Medicaid && IsExternalReview(appealType, appealLevel));

    // ── Default target ──────────────────────────────────────────────────

    /// <summary>Default target window for a first-level appeal of <paramref name="appealType"/>.</summary>
    public static TimeSpan MaxResponseWindow(AppealCoverage coverage, AppealType appealType, bool isUrgent) =>
        MaxResponseWindow(coverage, appealType, AppealLevel.FirstLevel, isUrgent);

    /// <summary>
    /// Default target window from receipt to decision — the conservative
    /// clock used when the caller supplies no target date. Never later than
    /// the enforceable maximum; for urgent and commercial grievances it is
    /// tighter than any federal ceiling (see <see cref="EnforceableMaximumWindow(AppealCoverage, AppealType, AppealLevel, bool)"/>).
    /// </summary>
    public static TimeSpan MaxResponseWindow(
        AppealCoverage coverage, AppealType appealType, AppealLevel appealLevel, bool isUrgent)
    {
        if (appealType == AppealType.Grievance)
        {
            return Clock(coverage) switch
            {
                ClockLine.MedicareAdvantage or ClockLine.PartD
                    => isUrgent ? TimeSpan.FromHours(24) : MedicareStandardGrievance,
                ClockLine.Medicaid => isUrgent ? Expedited : MedicaidStandardGrievance,
                _ => isUrgent ? Expedited : TimeSpan.FromDays(30)
            };
        }

        if (isUrgent) return Expedited;

        return StandardAppealWindow(coverage, appealType, appealLevel);
    }

    // ── Enforceable maximum ─────────────────────────────────────────────

    /// <summary>Federal ceiling for a first-level appeal of <paramref name="appealType"/>, or <c>null</c>.</summary>
    public static TimeSpan? EnforceableMaximumWindow(AppealCoverage coverage, AppealType appealType, bool isUrgent) =>
        EnforceableMaximumWindow(coverage, appealType, AppealLevel.FirstLevel, isUrgent);

    /// <summary>
    /// Federal ceiling on the time from receipt to decision, or <c>null</c>
    /// when no federal ceiling applies (commercial / Exchange grievances).
    /// For an expedited State Fair Hearing (a working-day clock) this is the
    /// widest possible span; use <see cref="ComputeEnforceableMaximum(DateTime, AppealCoverage, AppealType, AppealLevel, bool, IHolidayCalendar?)"/>
    /// for the exact instant.
    /// </summary>
    public static TimeSpan? EnforceableMaximumWindow(
        AppealCoverage coverage, AppealType appealType, AppealLevel appealLevel, bool isUrgent)
    {
        if (appealType == AppealType.Grievance)
        {
            return Clock(coverage) switch
            {
                ClockLine.MedicareAdvantage or ClockLine.PartD => MedicareStandardGrievance,
                ClockLine.Medicaid => MedicaidStandardGrievance,
                _ => null
            };
        }

        if (isUrgent)
        {
            return Clock(coverage) == ClockLine.Medicaid && IsExternalReview(appealType, appealLevel)
                ? StateFairHearingExpeditedCeiling
                : Expedited;
        }

        return StandardAppealWindow(coverage, appealType, appealLevel);
    }

    /// <summary>Standard (non-urgent) appeal clock — both default and ceiling.</summary>
    private static TimeSpan StandardAppealWindow(
        AppealCoverage coverage, AppealType appealType, AppealLevel appealLevel)
    {
        if (IsExternalReview(appealType, appealLevel))
        {
            return Clock(coverage) switch
            {
                ClockLine.MedicareAdvantage => StandardAppeal,                       // 42 CFR 422.592 (IRE)
                ClockLine.PartD => PartDStandardRedetermination,    // 42 CFR 423.600
                ClockLine.Medicaid => StateFairHearingStandard,             // 42 CFR 431.244(f)
                _ => AcaExternalReviewStandard                                   // 45 CFR 147.136(d)
            };
        }

        return Clock(coverage) == ClockLine.PartD
            ? PartDStandardRedetermination                                       // 42 CFR 423.590(a)
            : StandardAppeal;
    }

    // ── Instants ────────────────────────────────────────────────────────

    /// <summary>Default target response instant for a first-level appeal received at <paramref name="receivedAt"/>.</summary>
    public static DateTime ComputeTargetResponseDate(
        DateTime receivedAt, AppealCoverage coverage, AppealType appealType, bool isUrgent) =>
        ComputeTargetResponseDate(receivedAt, coverage, appealType, AppealLevel.FirstLevel, isUrgent);

    /// <summary>Default target response instant for an appeal received at <paramref name="receivedAt"/>.</summary>
    public static DateTime ComputeTargetResponseDate(
        DateTime receivedAt, AppealCoverage coverage, AppealType appealType, AppealLevel appealLevel, bool isUrgent) =>
        receivedAt + MaxResponseWindow(coverage, appealType, appealLevel, isUrgent);

    /// <summary>
    /// Latest permissible response instant under federal rules for a
    /// first-level appeal, or <c>null</c> when none applies.
    /// </summary>
    public static DateTime? ComputeEnforceableMaximum(
        DateTime receivedAt, AppealCoverage coverage, AppealType appealType, bool isUrgent,
        IHolidayCalendar? holidays = null) =>
        ComputeEnforceableMaximum(receivedAt, coverage, appealType, AppealLevel.FirstLevel, isUrgent, holidays);

    /// <summary>
    /// Latest permissible response instant under federal rules, or <c>null</c>
    /// when none applies. <paramref name="holidays"/> governs working-day
    /// clocks (expedited State Fair Hearing); <c>null</c> means U.S. federal
    /// holidays only.
    /// </summary>
    public static DateTime? ComputeEnforceableMaximum(
        DateTime receivedAt, AppealCoverage coverage, AppealType appealType, AppealLevel appealLevel, bool isUrgent,
        IHolidayCalendar? holidays = null)
    {
        if (IsExpeditedStateFairHearing(coverage, appealType, appealLevel, isUrgent))
            return AddWorkingDays(receivedAt, StateFairHearingExpeditedWorkingDays, holidays);

        return EnforceableMaximumWindow(coverage, appealType, appealLevel, isUrgent) is { } window
            ? receivedAt + window
            : null;
    }

    private static bool IsExpeditedStateFairHearing(
        AppealCoverage coverage, AppealType appealType, AppealLevel appealLevel, bool isUrgent) =>
        isUrgent
        && Clock(coverage) == ClockLine.Medicaid
        && IsExternalReview(appealType, appealLevel);

    /// <summary>
    /// <paramref name="start"/> moved forward by <paramref name="workingDays"/>
    /// working days, keeping the time of day (Friday 10:00 + 3 → Wednesday
    /// 10:00; Saturday 10:00 + 3 → Wednesday 10:00, one day later for each
    /// holiday crossed). A working day is a Monday–Friday that
    /// <paramref name="holidays"/> does not mark as a holiday; <c>null</c>
    /// means <see cref="UsFederalHolidayCalendar"/>. The date is taken from
    /// the instant as given (UTC for stored deadlines).
    /// </summary>
    public static DateTime AddWorkingDays(DateTime start, int workingDays, IHolidayCalendar? holidays = null)
    {
        holidays ??= UsFederalHolidayCalendar.Instance;
        var result = start;
        var remaining = workingDays;
        while (remaining > 0)
        {
            result = result.AddDays(1);
            if (IsWorkingDay(DateOnly.FromDateTime(result), holidays)) remaining--;
        }
        return result;
    }

    /// <summary>A Monday–Friday that is not a holiday in <paramref name="holidays"/> (default: U.S. federal).</summary>
    public static bool IsWorkingDay(DateOnly date, IHolidayCalendar? holidays = null) =>
        date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)
        && !(holidays ?? UsFederalHolidayCalendar.Instance).IsHoliday(date);

    // ── Extension ───────────────────────────────────────────────────────

    /// <summary>
    /// The one-time extension the plan may take for this combination.
    /// <see cref="AppealExtensionRule.MaxExtension"/> is
    /// <see cref="TimeSpan.Zero"/> when no extension is permitted;
    /// <see cref="AppealExtensionRule.RegulatoryBasis"/> cites the
    /// authority either way (non-PHI — safe for audit payloads and
    /// ProblemDetails).
    /// </summary>
    public static AppealExtensionRule GetExtensionRule(
        AppealCoverage coverage, AppealType appealType, AppealLevel appealLevel, bool isUrgent)
    {
        if (IsExternalReview(appealType, appealLevel))
        {
            return AppealExtensionRule.NotPermitted(Clock(coverage) == ClockLine.Medicaid
                ? "42 CFR 431.244(f): State Fair Hearing timeframes are State-controlled, not plan-controlled."
                : "External / independent review timeframes are controlled by the IRE or external reviewer, not the plan.");
        }

        if (appealType == AppealType.Grievance)
        {
            return Clock(coverage) switch
            {
                ClockLine.MedicareAdvantage when !isUrgent => AppealExtensionRule.Permitted("42 CFR 422.564(e)(2)"),
                ClockLine.PartD when !isUrgent => AppealExtensionRule.Permitted("42 CFR 423.564(e)(2)"),
                ClockLine.MedicareAdvantage or ClockLine.PartD => AppealExtensionRule.NotPermitted(
                    "42 CFR 422.564(f) / 423.564(f): expedited grievances may not be extended."),
                ClockLine.Medicaid => AppealExtensionRule.Permitted("42 CFR 438.408(c)"),
                _ => AppealExtensionRule.NotPermitted(
                    "Commercial / Exchange grievance timeframes are governed by state law; no federal extension.")
            };
        }

        return Clock(coverage) switch
        {
            ClockLine.MedicareAdvantage => AppealExtensionRule.Permitted("42 CFR 422.590(f)"),
            ClockLine.Medicaid => AppealExtensionRule.Permitted("42 CFR 438.408(c)"),
            ClockLine.PartD => AppealExtensionRule.NotPermitted(
                "42 CFR 423.590: Part D redetermination timeframes may not be extended."),
            _ => AppealExtensionRule.NotPermitted(
                "29 CFR 2560.503-1(i) / 45 CFR 147.136(b): group and individual health plans may not unilaterally extend appeal decision timeframes.")
        };
    }

    /// <summary>
    /// Latest instant an extended deadline may reach: the enforceable
    /// federal maximum from receipt plus the permitted extension (just the
    /// enforceable maximum when no extension is permitted). <c>null</c>
    /// when no federal ceiling applies. <paramref name="holidays"/> as in
    /// <see cref="ComputeEnforceableMaximum(DateTime, AppealCoverage, AppealType, AppealLevel, bool, IHolidayCalendar?)"/>.
    /// </summary>
    public static DateTime? ComputeMaxExtendedTargetResponseDate(
        DateTime receivedAt, AppealCoverage coverage, AppealType appealType, AppealLevel appealLevel, bool isUrgent,
        IHolidayCalendar? holidays = null) =>
        ComputeEnforceableMaximum(receivedAt, coverage, appealType, appealLevel, isUrgent, holidays) is { } max
            ? max + GetExtensionRule(coverage, appealType, appealLevel, isUrgent).MaxExtension
            : null;
}

/// <summary>Result of <see cref="AppealResponseDeadlinePolicy.GetExtensionRule"/>.</summary>
public sealed record AppealExtensionRule(TimeSpan MaxExtension, string RegulatoryBasis)
{
    public bool IsPermitted => MaxExtension > TimeSpan.Zero;

    internal static AppealExtensionRule Permitted(string basis) =>
        new(AppealResponseDeadlinePolicy.MaxExtension, basis);

    internal static AppealExtensionRule NotPermitted(string basis) => new(TimeSpan.Zero, basis);
}
