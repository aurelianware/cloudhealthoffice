using AppealsService.Models;

namespace AppealsService.Services;

/// <summary>
/// Pure regulatory response-deadline policy for appeals. Computes the
/// latest permissible <see cref="Appeal.TargetResponseDate"/> from line of
/// business, appeal type and urgency. No side effects; no clock access —
/// callers pass the receipt instant.
///
/// Federal maximums applied:
///   Expedited / urgent (every LOB)            72 hours
///     - Medicare Advantage: 42 CFR 422.590(e)
///     - Medicaid managed care: 42 CFR 438.408(b)(3)
///     - Commercial / Marketplace urgent care: 29 CFR 2560.503-1(i)(2)(i),
///       45 CFR 147.136(b)
///   Standard Medicare Advantage                30 days  (42 CFR 422.590(a), pre-service)
///   Standard Medicaid managed care             30 days  (42 CFR 438.408(b)(2))
///   Standard Commercial / Marketplace          30 days  (29 CFR 2560.503-1(i)(2)(ii), pre-service)
///
///   Grievance, Medicare Advantage              30 days / expedited 24 hours (42 CFR 422.564(e), (f))
///   Grievance, Medicaid managed care           90 days  (42 CFR 438.408(b)(1)); urgent: 72 hours (conservative)
///   Grievance, Commercial / Marketplace        no federal clock (state law); 30 days / 72 hours (conservative)
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

    /// <summary>Maximum permissible time from receipt to decision.</summary>
    public static TimeSpan MaxResponseWindow(LineOfBusiness lineOfBusiness, AppealType appealType, bool isUrgent)
    {
        if (appealType == AppealType.Grievance)
        {
            return lineOfBusiness switch
            {
                LineOfBusiness.Medicare => isUrgent ? TimeSpan.FromHours(24) : TimeSpan.FromDays(30),
                LineOfBusiness.Medicaid => isUrgent ? Expedited : TimeSpan.FromDays(90),
                _ => isUrgent ? Expedited : TimeSpan.FromDays(30)
            };
        }

        return isUrgent ? Expedited : StandardAppeal;
    }

    /// <summary>Latest permissible response instant for an appeal received at <paramref name="receivedAt"/>.</summary>
    public static DateTime ComputeTargetResponseDate(
        DateTime receivedAt, LineOfBusiness lineOfBusiness, AppealType appealType, bool isUrgent) =>
        receivedAt + MaxResponseWindow(lineOfBusiness, appealType, isUrgent);
}
