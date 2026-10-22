using CareManagementService.Models;

namespace CareManagementService.Services;

/// <summary>A case cannot move the way the caller asked.</summary>
public sealed class CareCaseTransitionException(CareCaseStatus from, CareCaseStatus to)
    : InvalidOperationException($"A care case cannot move from {from} to {to}.");

/// <summary>
/// The shape of a case's life, with no persistence and no clock of its own.
///
/// Two rules carry the weight. Closed and Declined are terminal: a member who
/// comes back gets a new case, because a case's dates and outcome are the
/// record of one episode and reopening would overwrite them. And a care plan
/// cannot be activated without the member's consent — care management is
/// voluntary, and a plan the member never agreed to is not a care plan.
/// </summary>
public static class CareCaseLifecycle
{
    public static bool CanTransition(CareCaseStatus from, CareCaseStatus to) =>
        Enum.IsDefined(from) && Enum.IsDefined(to) && (from == to || from switch
        {
            CareCaseStatus.Referred => to is CareCaseStatus.Screening or CareCaseStatus.Outreach
                or CareCaseStatus.Declined or CareCaseStatus.Closed,

            CareCaseStatus.Screening => to is CareCaseStatus.Outreach or CareCaseStatus.Active
                or CareCaseStatus.Declined or CareCaseStatus.Closed,

            CareCaseStatus.Outreach => to is CareCaseStatus.Active or CareCaseStatus.OnHold
                or CareCaseStatus.Declined or CareCaseStatus.Closed,

            CareCaseStatus.Active => to is CareCaseStatus.OnHold or CareCaseStatus.Closed,

            CareCaseStatus.OnHold => to is CareCaseStatus.Active or CareCaseStatus.Closed,

            // Terminal: a member who comes back gets a new case.
            CareCaseStatus.Closed or CareCaseStatus.Declined => false,

            _ => false,
        });

    /// <summary>
    /// Moves the case and records the move. Closing requires an outcome, and
    /// declining requires a reason, because both are the end of the record
    /// and an unexplained ending is not auditable.
    /// </summary>
    public static void Transition(
        CareCase careCase,
        CareCaseStatus target,
        DateTime nowUtc,
        string? actor = null,
        string? reason = null,
        CareCaseClosureOutcome? closureOutcome = null)
    {
        ArgumentNullException.ThrowIfNull(careCase);

        var current = careCase.Status;

        if (!CanTransition(current, target))
            throw new CareCaseTransitionException(current, target);

        if (target == CareCaseStatus.Active && !careCase.MemberConsentObtained)
            throw new InvalidOperationException(
                "A care case cannot be made active until the member has consented: care management is voluntary.");

        if (target == CareCaseStatus.Closed && closureOutcome is null)
            throw new ArgumentException("Closing a care case requires a closure outcome.");

        if (target == CareCaseStatus.Declined && string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("A declined care case requires the reason the member gave.");

        if (current == target) return;

        careCase.Status = target;
        careCase.LastUpdatedDate = nowUtc;
        careCase.LastUpdatedBy = actor ?? careCase.LastUpdatedBy;

        switch (target)
        {
            case CareCaseStatus.Active:
                careCase.OpenedDate ??= nowUtc;
                break;

            case CareCaseStatus.Closed:
                careCase.ClosedDate = nowUtc;
                careCase.ClosureOutcome = closureOutcome;
                careCase.ClosureReason = reason;
                CloseOutWork(careCase, nowUtc);
                break;

            case CareCaseStatus.Declined:
                careCase.ClosedDate = nowUtc;
                careCase.DeclineReason = reason;
                careCase.ClosureOutcome = CareCaseClosureOutcome.MemberWithdrew;
                CloseOutWork(careCase, nowUtc);
                break;

            case CareCaseStatus.OnHold when careCase.CarePlan?.Status == CarePlanStatus.Active:
                careCase.CarePlan.Status = CarePlanStatus.OnHold;
                break;
        }

        careCase.StatusHistory ??= new();
        careCase.StatusHistory.Add(new CareCaseStatusChange
        {
            Status = target,
            Reason = reason,
            ChangedAt = nowUtc,
            ChangedBy = actor,
        });
    }

    /// <summary>
    /// Records the member's consent. Dated, because the date is what makes it
    /// evidence rather than a flag.
    /// </summary>
    public static void RecordConsent(CareCase careCase, DateTime nowUtc, string? actor = null)
    {
        ArgumentNullException.ThrowIfNull(careCase);

        if (careCase.Status is CareCaseStatus.Closed or CareCaseStatus.Declined)
            throw new InvalidOperationException("A closed care case cannot record consent.");

        careCase.MemberConsentObtained = true;
        careCase.MemberConsentDate ??= nowUtc;
        careCase.LastUpdatedDate = nowUtc;
        careCase.LastUpdatedBy = actor ?? careCase.LastUpdatedBy;
    }

    /// <summary>
    /// A closing case leaves nothing open behind it: the plan is completed or
    /// cancelled depending on whether its goals were met, and outstanding
    /// activities are cancelled rather than left to age in the work queue.
    /// </summary>
    private static void CloseOutWork(CareCase careCase, DateTime nowUtc)
    {
        if (careCase.CarePlan is { } plan && plan.Status is CarePlanStatus.Draft or CarePlanStatus.Active or CarePlanStatus.OnHold)
        {
            plan.Status = plan.Status == CarePlanStatus.Draft
                ? CarePlanStatus.Cancelled
                : CarePlanStatus.Completed;
            plan.LastUpdatedDate = nowUtc;
        }

        foreach (var activity in careCase.Activities ?? [])
        {
            if (activity.Status == CareActivityStatus.Open)
                activity.Status = CareActivityStatus.Cancelled;
        }
    }
}
