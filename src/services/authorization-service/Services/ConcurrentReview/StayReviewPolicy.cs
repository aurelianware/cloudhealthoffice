using AuthorizationService.Models;

namespace AuthorizationService.Services.ConcurrentReview;

/// <summary>A stay cannot move the way the caller asked.</summary>
public sealed class InpatientStayTransitionException(string message) : InvalidOperationException(message);

/// <summary>
/// What a reviewer decided, before it has been checked against the stay.
/// </summary>
public sealed record ConcurrentReviewDecision
{
    public required ConcurrentReviewOutcome Outcome { get; init; }

    /// <summary>Days this review certifies. Must be positive to certify, zero otherwise.</summary>
    public int DaysCertified { get; init; }

    /// <summary>Level of care going forward. Null continues the stay's current level.</summary>
    public string? LevelOfCare { get; init; }

    /// <summary>
    /// When the next review falls due. Null places it on the last certified
    /// day, which is the latest it can be without the certification lapsing
    /// unreviewed.
    /// </summary>
    public DateTime? NextReviewDueDate { get; init; }

    public string? ReviewerId { get; init; }
    public string? ReviewerName { get; init; }
    public bool PhysicianReviewed { get; init; }
    public string? DenialReasonCode { get; init; }
    public string? Reason { get; init; }

    // ── The criteria behind it ────────────────────────────────────
    public string? CriteriaProviderId { get; init; }
    public string? CriteriaGuidelineId { get; init; }
    public string? CriteriaGuidelineVersion { get; init; }
    public string? CriteriaRecommendation { get; init; }
    public IReadOnlyList<string> CriteriaRationale { get; init; } = [];
}

/// <summary>
/// The rules of concurrent review, with no persistence and no clock of its
/// own. Everything the census and the watchdog need to know about a stay is
/// derived here, so both read the same answer.
///
/// Dates are inclusive UTC calendar dates throughout, matching the
/// authorization record's approved and expiration dates.
/// </summary>
public static class StayReviewPolicy
{
    /// <summary>
    /// Validates a decision against the stay and applies it, appending the
    /// review and moving the certification forward. Throws rather than
    /// recording something that cannot be defended later.
    /// </summary>
    public static ConcurrentReviewRecord ApplyReview(
        InpatientStay stay, ConcurrentReviewDecision decision, DateTime nowUtc, string? actor = null)
    {
        ArgumentNullException.ThrowIfNull(stay);
        ArgumentNullException.ThrowIfNull(decision);

        if (stay.Status != InpatientStayStatus.Admitted)
            throw new InpatientStayTransitionException(
                $"A {stay.Status.ToString().ToLowerInvariant()} stay cannot be reviewed.");

        if (!Enum.IsDefined(decision.Outcome))
            throw new ArgumentException("Unknown concurrent review outcome.");

        var certifies = decision.Outcome is ConcurrentReviewOutcome.Approved or ConcurrentReviewOutcome.Modified;

        if (certifies && decision.DaysCertified <= 0)
            throw new ArgumentException("An approved or modified review must certify at least one day.");

        if (!certifies && decision.DaysCertified != 0)
            throw new ArgumentException("A denied or pended review cannot certify days.");

        // An adverse determination needs a reviewer of equal or greater
        // clinical qualification, and a reason the member can be told.
        var adverse = decision.Outcome is ConcurrentReviewOutcome.Modified or ConcurrentReviewOutcome.Denied;
        if (adverse)
        {
            if (!decision.PhysicianReviewed)
                throw new ArgumentException("An adverse concurrent review requires a physician reviewer.");
            if (string.IsNullOrWhiteSpace(decision.Reason))
                throw new ArgumentException("An adverse concurrent review requires a reason.");
        }

        DateTime? certifiedThrough = stay.CertifiedThroughDate;

        if (certifies)
        {
            // Certified days are contiguous and never overlap a prior
            // certification: the next day begins where the last one ended, or
            // on the admission date for the first certification.
            var start = certifiedThrough?.Date.AddDays(1) ?? stay.AdmissionDate.Date;
            certifiedThrough = start.AddDays(decision.DaysCertified - 1);
        }

        var nextReviewDue = decision.NextReviewDueDate?.Date;
        if (certifies)
        {
            // Defaulting to the last certified day is the latest a review can
            // fall without the certification lapsing unreviewed.
            nextReviewDue ??= certifiedThrough!.Value;

            if (nextReviewDue > certifiedThrough)
                throw new ArgumentException(
                    "The next review cannot fall after the last certified day, or the certification lapses unreviewed.");
            if (nextReviewDue < stay.AdmissionDate.Date)
                throw new ArgumentException("The next review cannot fall before the admission date.");
        }
        else
        {
            // A denial ends the stay's certification; a pend leaves the
            // existing review date alone so the stay stays on the worklist.
            nextReviewDue = decision.Outcome == ConcurrentReviewOutcome.Denied
                ? null
                : decision.NextReviewDueDate?.Date ?? stay.NextReviewDueDate?.Date;
        }

        var record = new ConcurrentReviewRecord
        {
            ReviewNumber             = (stay.Reviews?.Count ?? 0) + 1,
            ReviewedAt               = nowUtc,
            Outcome                  = decision.Outcome,
            DaysCertified            = decision.DaysCertified,
            CertifiedThroughDate     = certifies ? certifiedThrough : null,
            LevelOfCare              = decision.LevelOfCare ?? stay.LevelOfCare,
            NextReviewDueDate        = nextReviewDue,
            CriteriaProviderId       = decision.CriteriaProviderId,
            CriteriaGuidelineId      = decision.CriteriaGuidelineId,
            CriteriaGuidelineVersion = decision.CriteriaGuidelineVersion,
            CriteriaRecommendation   = decision.CriteriaRecommendation,
            CriteriaRationale        = decision.CriteriaRationale.ToList(),
            ReviewerId               = decision.ReviewerId ?? actor,
            ReviewerName             = decision.ReviewerName,
            PhysicianReviewed        = decision.PhysicianReviewed,
            DenialReasonCode         = decision.DenialReasonCode,
            Reason                   = decision.Reason,
        };

        stay.Reviews ??= new();
        stay.Reviews.Add(record);

        if (certifies)
        {
            stay.CertifiedThroughDate = certifiedThrough;
            stay.CertifiedDays += decision.DaysCertified;
            stay.LevelOfCare = record.LevelOfCare;
        }

        stay.NextReviewDueDate = nextReviewDue;
        stay.LastUpdatedDate = nowUtc;
        stay.LastUpdatedBy = actor ?? stay.LastUpdatedBy;

        return record;
    }

    /// <summary>Records a discharge. Nothing further is reviewed on the stay.</summary>
    public static void Discharge(
        InpatientStay stay, DateTime dischargeDate, string? disposition, DateTime nowUtc, string? actor = null)
    {
        ArgumentNullException.ThrowIfNull(stay);

        if (stay.Status != InpatientStayStatus.Admitted)
            throw new InpatientStayTransitionException(
                $"A {stay.Status.ToString().ToLowerInvariant()} stay cannot be discharged.");

        var discharged = dischargeDate.Date;

        if (discharged < stay.AdmissionDate.Date)
            throw new ArgumentException("A discharge cannot precede the admission.");
        if (discharged > nowUtc.Date)
            throw new ArgumentException("A discharge cannot be in the future.");

        stay.Status = InpatientStayStatus.Discharged;
        stay.DischargeDate = discharged;
        stay.DischargeDisposition = disposition;
        stay.NextReviewDueDate = null;
        stay.LastUpdatedDate = nowUtc;
        stay.LastUpdatedBy = actor ?? stay.LastUpdatedBy;
    }

    /// <summary>Days the member has been in the facility, counting admission and discharge days.</summary>
    public static int ElapsedDays(InpatientStay stay, DateTime asOfUtc)
    {
        var end = stay.DischargeDate?.Date is { } discharged && discharged < asOfUtc.Date
            ? discharged
            : asOfUtc.Date;

        var days = (end - stay.AdmissionDate.Date).Days + 1;
        return days < 0 ? 0 : days;
    }

    /// <summary>Elapsed days nobody has certified. The number a UM manager is actually watching.</summary>
    public static int UncertifiedDays(InpatientStay stay, DateTime asOfUtc) =>
        Math.Max(0, ElapsedDays(stay, asOfUtc) - stay.CertifiedDays);

    /// <summary>True when the stay is open and its review date has passed.</summary>
    public static bool IsReviewOverdue(InpatientStay stay, DateTime asOfUtc) =>
        stay.Status == InpatientStayStatus.Admitted
        && stay.NextReviewDueDate?.Date < asOfUtc.Date;

    /// <summary>
    /// True when the stay is open and the member is in a bed on days nobody
    /// has certified — either nothing was ever certified, or the certification
    /// has run out behind them.
    /// </summary>
    public static bool IsCertificationLapsed(InpatientStay stay, DateTime asOfUtc) =>
        stay.Status == InpatientStayStatus.Admitted
        && (stay.CertifiedThroughDate is null || stay.CertifiedThroughDate.Value.Date < asOfUtc.Date);
}
