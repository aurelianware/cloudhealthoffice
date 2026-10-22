using CareManagementService.Models;

namespace CareManagementService.Services;

/// <summary>Summary of where a plan has got to, for a case list or a dashboard.</summary>
public sealed record CarePlanProgress
{
    public required int TotalGoals        { get; init; }
    public required int AchievedGoals     { get; init; }
    public required int InProgressGoals   { get; init; }
    public required int OverdueGoals      { get; init; }
    public required int OpenBarriers      { get; init; }

    /// <summary>
    /// Share of pursued goals that were met, 0 to 1. Cancelled and proposed
    /// goals are left out of both halves: a goal that was never pursued is
    /// neither a success nor a failure, and counting it either way would make
    /// a care manager's numbers move when they tidy up a plan.
    /// </summary>
    public required decimal CompletionRate { get; init; }
}

/// <summary>
/// The rules of a care plan: what makes one coherent, when it may be
/// activated, and how far along it is.
/// </summary>
public static class CarePlanPolicy
{
    /// <summary>
    /// Checks the plan hangs together before it is saved. The references
    /// between problems, goals and interventions are ids rather than nesting,
    /// so nothing else stops a goal pointing at a problem that was deleted.
    /// </summary>
    public static void Validate(CarePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var problemIds = new HashSet<string>(plan.Problems.Select(p => p.Id), StringComparer.OrdinalIgnoreCase);
        var goalIds = new HashSet<string>(plan.Goals.Select(g => g.Id), StringComparer.OrdinalIgnoreCase);

        if (problemIds.Count != plan.Problems.Count)
            throw new ArgumentException("Care plan problem ids must be unique.");
        if (goalIds.Count != plan.Goals.Count)
            throw new ArgumentException("Care plan goal ids must be unique.");
        if (plan.Interventions.Select(i => i.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != plan.Interventions.Count)
            throw new ArgumentException("Care plan intervention ids must be unique.");

        foreach (var goal in plan.Goals)
        {
            var dangling = goal.ProblemIds.FirstOrDefault(id => !problemIds.Contains(id));
            if (dangling is not null)
                throw new ArgumentException($"Goal '{goal.Id}' references problem '{dangling}', which is not on the plan.");
        }

        foreach (var intervention in plan.Interventions)
        {
            var dangling = intervention.GoalIds.FirstOrDefault(id => !goalIds.Contains(id));
            if (dangling is not null)
                throw new ArgumentException(
                    $"Intervention '{intervention.Id}' references goal '{dangling}', which is not on the plan.");

            if (intervention.EndDate is { } end && intervention.StartDate is { } start && end < start)
                throw new ArgumentException($"Intervention '{intervention.Id}' ends before it starts.");
        }
    }

    /// <summary>
    /// Activates the plan with the member. Requires the member's consent and
    /// at least one goal: a plan with no goal is a note, and activating it
    /// would report care management that is not happening.
    /// </summary>
    public static void Activate(CareCase careCase, DateTime nowUtc, string? actor = null)
    {
        ArgumentNullException.ThrowIfNull(careCase);

        var plan = careCase.CarePlan
            ?? throw new InvalidOperationException("There is no care plan on this case to activate.");

        if (!careCase.MemberConsentObtained)
            throw new InvalidOperationException(
                "A care plan cannot be activated until the member has consented: care management is voluntary.");

        if (careCase.Status is CareCaseStatus.Closed or CareCaseStatus.Declined)
            throw new InvalidOperationException("A closed care case cannot have its plan activated.");

        if (plan.Goals.Count == 0)
            throw new InvalidOperationException("A care plan cannot be activated without at least one goal.");

        Validate(plan);

        plan.Status = CarePlanStatus.Active;
        plan.ActivatedDate ??= nowUtc;
        plan.LastUpdatedDate = nowUtc;
        plan.LastUpdatedBy = actor ?? plan.LastUpdatedBy;

        // A proposed goal becomes one the member has agreed to work on.
        foreach (var goal in plan.Goals.Where(g => g.Status == CareGoalStatus.Proposed))
            goal.Status = CareGoalStatus.InProgress;
    }

    /// <summary>
    /// Records progress against a goal. The note is required: a status change
    /// with no explanation tells the next care manager nothing.
    /// </summary>
    public static CareGoalProgress RecordGoalProgress(
        CarePlan plan, string goalId, CareGoalStatus status, string note, DateTime nowUtc, string? actor = null)
    {
        ArgumentNullException.ThrowIfNull(plan);

        if (string.IsNullOrWhiteSpace(note))
            throw new ArgumentException("Recording goal progress requires a note.");
        if (!Enum.IsDefined(status))
            throw new ArgumentException("Unknown goal status.");

        var goal = plan.Goals.FirstOrDefault(g => string.Equals(g.Id, goalId, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException($"Goal '{goalId}' is not on this care plan.");

        var entry = new CareGoalProgress
        {
            RecordedAt = nowUtc,
            Status = status,
            Note = note,
            RecordedBy = actor,
        };

        goal.Progress.Add(entry);
        goal.Status = status;

        if (status == CareGoalStatus.Achieved) goal.AchievedDate ??= nowUtc;
        else goal.AchievedDate = null;

        plan.LastUpdatedDate = nowUtc;
        plan.LastUpdatedBy = actor ?? plan.LastUpdatedBy;

        return entry;
    }

    /// <summary>Where the plan has got to.</summary>
    public static CarePlanProgress Summarize(CarePlan? plan, DateTime asOfUtc)
    {
        if (plan is null)
            return new CarePlanProgress
            {
                TotalGoals = 0, AchievedGoals = 0, InProgressGoals = 0,
                OverdueGoals = 0, OpenBarriers = 0, CompletionRate = 0m,
            };

        var achieved = plan.Goals.Count(g => g.Status == CareGoalStatus.Achieved);
        var pursued = plan.Goals.Count(g => g.Status is CareGoalStatus.Achieved
                                              or CareGoalStatus.NotAchieved
                                              or CareGoalStatus.InProgress);

        return new CarePlanProgress
        {
            TotalGoals      = plan.Goals.Count,
            AchievedGoals   = achieved,
            InProgressGoals = plan.Goals.Count(g => g.Status == CareGoalStatus.InProgress),
            OverdueGoals    = plan.Goals.Count(g =>
                                  g.Status == CareGoalStatus.InProgress
                                  && g.TargetDate?.Date < asOfUtc.Date),
            OpenBarriers    = plan.Barriers.Count(b => !b.Resolved),
            CompletionRate  = pursued == 0 ? 0m : Math.Round((decimal)achieved / pursued, 4),
        };
    }
}
