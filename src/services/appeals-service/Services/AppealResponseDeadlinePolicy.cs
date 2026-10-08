using AppealsService.Models;

namespace AppealsService.Services;

/// <summary>
/// Pure regulatory response-deadline policy for appeals. No side effects;
/// no clock access — callers pass the receipt instant.
///
/// Two distinct answers:
///   - <b>Default target</b> (<see cref="MaxResponseWindow"/> /
///     <see cref="ComputeTargetResponseDate"/>): the conservative clock
///     applied when the caller supplies no <see cref="Appeal.TargetResponseDate"/>.
///   - <b>Enforceable maximum</b> (<see cref="EnforceableMaximumWindow"/> /
///     <see cref="ComputeEnforceableMaximum"/>): a genuine federal ceiling.
///     A caller-supplied date later than this is rejected. <c>null</c>
///     means no federal ceiling exists and caller dates are not rejected.
///
///                                       Default target   Enforceable max
///   Appeal, expedited (every LOB)       72 hours         72 hours
///     42 CFR 422.590(e); 42 CFR 438.408(b)(3);
///     29 CFR 2560.503-1(i)(2)(i); 45 CFR 147.136(b)
///   Appeal, standard (every LOB)        30 days          30 days
///     42 CFR 422.590(a) (MA pre-service); 42 CFR 438.408(b)(2);
///     29 CFR 2560.503-1(i)(2)(ii) (pre-service)
///   Grievance, MA standard              30 days          30 days  (42 CFR 422.564(e))
///   Grievance, MA urgent                24 hours         30 days
///     (422.564(f)'s 24h applies only to grievances about an extension or
///     a refusal to expedite, which the model cannot tell apart — so 24h is
///     a default only and the standard-grievance ceiling is enforced)
///   Grievance, Medicaid standard        90 days          90 days  (42 CFR 438.408(b)(1))
///   Grievance, Medicaid urgent          72 hours         90 days  (no expedited grievance clock)
///   Grievance, Commercial/Marketplace   30d / 72h        none     (state law; no federal clock)
/// </summary>
/// <remarks>
/// Conservative defaults: the appeal model does not distinguish pre-service
/// from post-service / payment appeals, so the shorter pre-service clock
/// (30 days) is applied to every standard appeal even though MA payment
/// reconsiderations (42 CFR 422.590(b)) and commercial post-service appeals
/// (29 CFR 2560.503-1(i)(2)(iii)) allow 60 days. <see cref="LineOfBusiness.Medicare"/>
/// is treated as Medicare Advantage (Part C); Part D redeterminations
/// (7 days standard, 42 CFR 423.590) are not modeled. External review /
/// IRE clocks are not modeled — those requests fall through to the
/// internal-appeal clock, which is the shorter one.
/// </remarks>
public static class AppealResponseDeadlinePolicy
{
    public static readonly TimeSpan Expedited = TimeSpan.FromHours(72);
    public static readonly TimeSpan StandardAppeal = TimeSpan.FromDays(30);
    public static readonly TimeSpan MedicareStandardGrievance = TimeSpan.FromDays(30);
    public static readonly TimeSpan MedicaidStandardGrievance = TimeSpan.FromDays(90);

    /// <summary>
    /// Default target window from receipt to decision — the conservative
    /// clock used when the caller supplies no target date. For appeals and
    /// standard MA / Medicaid grievances this equals the enforceable maximum;
    /// for urgent and commercial grievances it is tighter than any federal
    /// ceiling (see <see cref="EnforceableMaximumWindow"/>).
    /// </summary>
    public static TimeSpan MaxResponseWindow(LineOfBusiness lineOfBusiness, AppealType appealType, bool isUrgent)
    {
        if (appealType == AppealType.Grievance)
        {
            return lineOfBusiness switch
            {
                LineOfBusiness.Medicare => isUrgent ? TimeSpan.FromHours(24) : MedicareStandardGrievance,
                LineOfBusiness.Medicaid => isUrgent ? Expedited : MedicaidStandardGrievance,
                _ => isUrgent ? Expedited : TimeSpan.FromDays(30)
            };
        }

        return isUrgent ? Expedited : StandardAppeal;
    }

    /// <summary>
    /// Federal ceiling on the time from receipt to decision, or <c>null</c>
    /// when no federal ceiling applies (commercial / Marketplace grievances).
    /// </summary>
    public static TimeSpan? EnforceableMaximumWindow(LineOfBusiness lineOfBusiness, AppealType appealType, bool isUrgent)
    {
        if (appealType == AppealType.Grievance)
        {
            return lineOfBusiness switch
            {
                LineOfBusiness.Medicare => MedicareStandardGrievance,
                LineOfBusiness.Medicaid => MedicaidStandardGrievance,
                _ => null
            };
        }

        return isUrgent ? Expedited : StandardAppeal;
    }

    /// <summary>Default target response instant for an appeal received at <paramref name="receivedAt"/>.</summary>
    public static DateTime ComputeTargetResponseDate(
        DateTime receivedAt, LineOfBusiness lineOfBusiness, AppealType appealType, bool isUrgent) =>
        receivedAt + MaxResponseWindow(lineOfBusiness, appealType, isUrgent);

    /// <summary>
    /// Latest permissible response instant under federal rules, or <c>null</c>
    /// when none applies.
    /// </summary>
    public static DateTime? ComputeEnforceableMaximum(
        DateTime receivedAt, LineOfBusiness lineOfBusiness, AppealType appealType, bool isUrgent) =>
        EnforceableMaximumWindow(lineOfBusiness, appealType, isUrgent) is { } window
            ? receivedAt + window
            : null;
}
