using AppealsService.Models;

namespace AppealsService.Services;

/// <summary>
/// Pure regulatory response-deadline policy for appeals. Computes the
/// latest permissible <see cref="Appeal.TargetResponseDate"/> from line of
/// business, appeal type, appeal level and urgency, and the one-time
/// extension each combination permits. No side effects; no clock access —
/// callers pass the receipt instant.
///
/// Internal (plan-level) federal maximums:
///   Expedited / urgent (every LOB)            72 hours
///     - Medicare Advantage: 42 CFR 422.590(e)
///     - Medicare Part D: 42 CFR 423.590(d)
///     - Medicaid managed care: 42 CFR 438.408(b)(3)
///     - Commercial / Marketplace urgent care: 29 CFR 2560.503-1(i)(2)(i),
///       45 CFR 147.136(b)
///   Standard Medicare Advantage                30 days  (42 CFR 422.590(a), pre-service)
///   Standard Medicare Part D                    7 days  (42 CFR 423.590(a), benefit redetermination)
///   Standard Medicaid managed care             30 days  (42 CFR 438.408(b)(2))
///   Standard Commercial / Marketplace          30 days  (29 CFR 2560.503-1(i)(2)(ii), pre-service)
///
///   Grievance, Medicare Advantage / Part D     30 days / expedited 24 hours
///                                              (42 CFR 422.564(e), (f); 423.564(e), (f))
///   Grievance, Medicaid managed care           90 days  (42 CFR 438.408(b)(1)); urgent: 72 hours (conservative)
///   Grievance, Commercial / Marketplace        no federal clock (state law); 30 days / 72 hours (conservative)
///
/// External review (<see cref="AppealLevel.ExternalReview"/> or
/// <see cref="AppealType.ExternalReview"/>):
///   Medicare Advantage IRE reconsideration     30 days / 72 hours (42 CFR 422.592; IRE applies the
///                                              422.590 clocks)
///   Medicare Part D IRE reconsideration         7 days / 72 hours (42 CFR 423.600)
///   Commercial / Marketplace (ACA) external    45 days / 72 hours (45 CFR 147.136(d)(2)-(3);
///     review                                   29 CFR 2590.715-2719(d) for ERISA plans)
///   Medicaid State Fair Hearing                90 days / 72 hours (42 CFR 431.244(f); 438.408(f))
///
/// Extensions (one per appeal, see <see cref="GetExtensionRule"/>):
///   Medicare Advantage appeals                 up to 14 days, standard AND expedited (42 CFR 422.590(f))
///   Medicare Advantage / Part D grievances     up to 14 days, standard only (42 CFR 422.564(e)(2),
///                                              423.564(e)(2)); none for 24-hour expedited grievances
///   Medicaid managed care appeals/grievances   up to 14 days (42 CFR 438.408(c))
///   Medicare Part D redeterminations           none (42 CFR 423.590 has no extension provision)
///   Commercial / Marketplace                   none — 29 CFR 2560.503-1(i) does not permit a plan to
///                                              unilaterally extend appeal decision periods
///                                              (narrow multiemployer-board exception not modeled)
///   Any external review / State Fair Hearing   none — the IRE / external reviewer / State controls
///                                              that clock, not the plan
/// </summary>
/// <remarks>
/// Conservative defaults: the appeal model does not distinguish pre-service
/// from post-service / payment appeals, so the shorter pre-service clock
/// is applied to every standard appeal even though MA payment
/// reconsiderations (42 CFR 422.590(b)), Part D payment redeterminations
/// (14 days, 42 CFR 423.590(b)) and commercial post-service appeals
/// (29 CFR 2560.503-1(i)(2)(iii)) allow longer. Likewise the model has no
/// Part B drug discriminator, so MA extensions are allowed even though
/// 42 CFR 422.590(f) excludes Part B drug requests; callers handling a
/// Part B drug request must not invoke the extension operation.
/// <see cref="LineOfBusiness.Medicare"/> is Medicare Advantage (Part C);
/// <see cref="LineOfBusiness.MedicarePartD"/> covers Part D (including
/// MA-PD drug coverage).
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
/// the tracked date is informational. Expedited hearings are due within
/// 3 working days of the State's receipt of the case file
/// (431.244(f)(2)); 72 hours is the conservative calendar equivalent.
/// </remarks>
public static class AppealResponseDeadlinePolicy
{
    public static readonly TimeSpan Expedited = TimeSpan.FromHours(72);
    public static readonly TimeSpan StandardAppeal = TimeSpan.FromDays(30);
    public static readonly TimeSpan PartDStandardRedetermination = TimeSpan.FromDays(7);
    public static readonly TimeSpan AcaExternalReviewStandard = TimeSpan.FromDays(45);
    public static readonly TimeSpan StateFairHearingStandard = TimeSpan.FromDays(90);
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
    public static bool IsPlanControlled(LineOfBusiness lineOfBusiness, AppealType appealType, AppealLevel appealLevel) =>
        !(lineOfBusiness == LineOfBusiness.Medicaid && IsExternalReview(appealType, appealLevel));

    /// <summary>Maximum permissible time from receipt to decision for a first-level appeal of <paramref name="appealType"/>.</summary>
    public static TimeSpan MaxResponseWindow(LineOfBusiness lineOfBusiness, AppealType appealType, bool isUrgent) =>
        MaxResponseWindow(lineOfBusiness, appealType, AppealLevel.FirstLevel, isUrgent);

    /// <summary>Maximum permissible time from receipt to decision.</summary>
    public static TimeSpan MaxResponseWindow(
        LineOfBusiness lineOfBusiness, AppealType appealType, AppealLevel appealLevel, bool isUrgent)
    {
        if (appealType == AppealType.Grievance)
        {
            return lineOfBusiness switch
            {
                LineOfBusiness.Medicare or LineOfBusiness.MedicarePartD
                    => isUrgent ? TimeSpan.FromHours(24) : TimeSpan.FromDays(30),
                LineOfBusiness.Medicaid => isUrgent ? Expedited : TimeSpan.FromDays(90),
                _ => isUrgent ? Expedited : TimeSpan.FromDays(30)
            };
        }

        if (isUrgent) return Expedited;

        if (IsExternalReview(appealType, appealLevel))
        {
            return lineOfBusiness switch
            {
                LineOfBusiness.Medicare => StandardAppeal,                       // 42 CFR 422.592 (IRE)
                LineOfBusiness.MedicarePartD => PartDStandardRedetermination,    // 42 CFR 423.600
                LineOfBusiness.Medicaid => StateFairHearingStandard,             // 42 CFR 431.244(f)
                _ => AcaExternalReviewStandard                                   // 45 CFR 147.136(d)
            };
        }

        return lineOfBusiness == LineOfBusiness.MedicarePartD
            ? PartDStandardRedetermination
            : StandardAppeal;
    }

    /// <summary>Latest permissible response instant for a first-level appeal received at <paramref name="receivedAt"/>.</summary>
    public static DateTime ComputeTargetResponseDate(
        DateTime receivedAt, LineOfBusiness lineOfBusiness, AppealType appealType, bool isUrgent) =>
        ComputeTargetResponseDate(receivedAt, lineOfBusiness, appealType, AppealLevel.FirstLevel, isUrgent);

    /// <summary>Latest permissible response instant for an appeal received at <paramref name="receivedAt"/>.</summary>
    public static DateTime ComputeTargetResponseDate(
        DateTime receivedAt, LineOfBusiness lineOfBusiness, AppealType appealType, AppealLevel appealLevel, bool isUrgent) =>
        receivedAt + MaxResponseWindow(lineOfBusiness, appealType, appealLevel, isUrgent);

    /// <summary>
    /// The one-time extension the plan may take for this combination.
    /// <see cref="AppealExtensionRule.MaxExtension"/> is
    /// <see cref="TimeSpan.Zero"/> when no extension is permitted;
    /// <see cref="AppealExtensionRule.RegulatoryBasis"/> cites the
    /// authority either way (non-PHI — safe for audit payloads and
    /// ProblemDetails).
    /// </summary>
    public static AppealExtensionRule GetExtensionRule(
        LineOfBusiness lineOfBusiness, AppealType appealType, AppealLevel appealLevel, bool isUrgent)
    {
        if (IsExternalReview(appealType, appealLevel))
        {
            return AppealExtensionRule.NotPermitted(lineOfBusiness == LineOfBusiness.Medicaid
                ? "42 CFR 431.244(f): State Fair Hearing timeframes are State-controlled, not plan-controlled."
                : "External / independent review timeframes are controlled by the IRE or external reviewer, not the plan.");
        }

        if (appealType == AppealType.Grievance)
        {
            return lineOfBusiness switch
            {
                LineOfBusiness.Medicare when !isUrgent => AppealExtensionRule.Permitted("42 CFR 422.564(e)(2)"),
                LineOfBusiness.MedicarePartD when !isUrgent => AppealExtensionRule.Permitted("42 CFR 423.564(e)(2)"),
                LineOfBusiness.Medicare or LineOfBusiness.MedicarePartD => AppealExtensionRule.NotPermitted(
                    "42 CFR 422.564(f) / 423.564(f): expedited grievances (24 hours) may not be extended."),
                LineOfBusiness.Medicaid => AppealExtensionRule.Permitted("42 CFR 438.408(c)"),
                _ => AppealExtensionRule.NotPermitted(
                    "Commercial / Marketplace grievance timeframes are governed by state law; no federal extension.")
            };
        }

        return lineOfBusiness switch
        {
            LineOfBusiness.Medicare => AppealExtensionRule.Permitted("42 CFR 422.590(f)"),
            LineOfBusiness.Medicaid => AppealExtensionRule.Permitted("42 CFR 438.408(c)"),
            LineOfBusiness.MedicarePartD => AppealExtensionRule.NotPermitted(
                "42 CFR 423.590: Part D redetermination timeframes may not be extended."),
            _ => AppealExtensionRule.NotPermitted(
                "29 CFR 2560.503-1(i) / 45 CFR 147.136(b): group and individual health plans may not unilaterally extend appeal decision timeframes.")
        };
    }

    /// <summary>
    /// Latest instant an extended deadline may reach: the regulatory
    /// maximum from receipt plus the permitted extension. Equals the
    /// unextended maximum when no extension is permitted.
    /// </summary>
    public static DateTime ComputeMaxExtendedTargetResponseDate(
        DateTime receivedAt, LineOfBusiness lineOfBusiness, AppealType appealType, AppealLevel appealLevel, bool isUrgent) =>
        ComputeTargetResponseDate(receivedAt, lineOfBusiness, appealType, appealLevel, isUrgent)
        + GetExtensionRule(lineOfBusiness, appealType, appealLevel, isUrgent).MaxExtension;
}

/// <summary>Result of <see cref="AppealResponseDeadlinePolicy.GetExtensionRule"/>.</summary>
public sealed record AppealExtensionRule(TimeSpan MaxExtension, string RegulatoryBasis)
{
    public bool IsPermitted => MaxExtension > TimeSpan.Zero;

    internal static AppealExtensionRule Permitted(string basis) =>
        new(AppealResponseDeadlinePolicy.MaxExtension, basis);

    internal static AppealExtensionRule NotPermitted(string basis) => new(TimeSpan.Zero, basis);
}
