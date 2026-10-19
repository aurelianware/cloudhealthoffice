namespace AppealsService.Services.RulePacks;

/// <summary>Turns a <see cref="Clock"/> into an instant.</summary>
public static class RulePackClock
{
    /// <summary>
    /// <paramref name="start"/> moved forward by <paramref name="clock"/>.
    /// Business and working days skip weekends and <paramref name="holidays"/>
    /// (default: U.S. federal), keeping the time of day, the same arithmetic
    /// as <see cref="AppealResponseDeadlinePolicy.AddWorkingDays"/>.
    /// </summary>
    public static DateTime Due(DateTime start, Clock clock, IHolidayCalendar? holidays = null) => clock.Unit switch
    {
        ClockUnit.Hours => start.AddHours(clock.Amount),
        ClockUnit.CalendarDays => start.AddDays(clock.Amount),
        ClockUnit.BusinessDays or ClockUnit.WorkingDays =>
            AppealResponseDeadlinePolicy.AddWorkingDays(start, clock.Amount, holidays),
        ClockUnit.Months => start.AddMonths(clock.Amount),
        _ => throw new ArgumentOutOfRangeException(nameof(clock), clock.Unit, "Unknown clock unit.")
    };
}
