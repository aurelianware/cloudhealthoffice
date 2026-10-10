namespace PaymentService.Services;

/// <summary>
/// ACH settlement days: Monday to Friday except the Federal Reserve holidays
/// (New Year's Day, Martin Luther King Jr. Day, Presidents Day, Memorial Day,
/// Juneteenth, Independence Day, Labor Day, Columbus Day, Veterans Day,
/// Thanksgiving, Christmas). A holiday on a Sunday is observed the Monday after;
/// one on a Saturday is not moved (the Federal Reserve is open the Friday before).
/// </summary>
public static class AchBankingCalendar
{
    public static bool IsBankingDay(DateTime date)
    {
        var d = date.Date;
        if (d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            return false;
        return !Holidays(d.Year).Contains(d);
    }

    public static IReadOnlySet<DateTime> Holidays(int year)
    {
        var fixedDates = new[]
        {
            new DateTime(year, 1, 1), new DateTime(year, 6, 19), new DateTime(year, 7, 4),
            new DateTime(year, 11, 11), new DateTime(year, 12, 25),
        };
        var set = new HashSet<DateTime>(fixedDates.Select(d => d.DayOfWeek == DayOfWeek.Sunday ? d.AddDays(1) : d))
        {
            Nth(year, 1, DayOfWeek.Monday, 3),   // Martin Luther King Jr. Day
            Nth(year, 2, DayOfWeek.Monday, 3),   // Presidents Day
            Last(year, 5, DayOfWeek.Monday),     // Memorial Day
            Nth(year, 9, DayOfWeek.Monday, 1),   // Labor Day
            Nth(year, 10, DayOfWeek.Monday, 2),  // Columbus Day
            Nth(year, 11, DayOfWeek.Thursday, 4), // Thanksgiving
        };
        return set;
    }

    private static DateTime Nth(int year, int month, DayOfWeek day, int n)
    {
        var first = new DateTime(year, month, 1);
        var offset = ((int)day - (int)first.DayOfWeek + 7) % 7;
        return first.AddDays(offset + 7 * (n - 1));
    }

    private static DateTime Last(int year, int month, DayOfWeek day)
    {
        var last = new DateTime(year, month, DateTime.DaysInMonth(year, month));
        var offset = ((int)last.DayOfWeek - (int)day + 7) % 7;
        return last.AddDays(-offset);
    }
}
