using System.Collections.Concurrent;
using System.Globalization;

namespace AppealsService.Services;

/// <summary>
/// Non-working days other than Saturdays and Sundays. Working-day
/// arithmetic (<see cref="AppealResponseDeadlinePolicy.AddWorkingDays"/>)
/// skips weekends itself and asks the calendar about every weekday.
/// </summary>
public interface IHolidayCalendar
{
    /// <summary>True when <paramref name="date"/> is a holiday (as observed).</summary>
    bool IsHoliday(DateOnly date);
}

/// <summary>
/// The eleven U.S. federal holidays of 5 U.S.C. 6103(a), computed by rule
/// for any year, with the observed-date shift of 5 U.S.C. 6103(b) and
/// Executive Order 11582: a holiday falling on a Saturday is observed the
/// Friday before, one falling on a Sunday the Monday after. New Year's Day
/// on a Saturday is therefore observed on December 31 of the prior year.
/// Only the observed date is a non-working day. Birthday of Martin Luther
/// King, Jr. applies from 1986 and Juneteenth from 2021, the first years
/// they were observed.
/// </summary>
public sealed class UsFederalHolidayCalendar : IHolidayCalendar
{
    public static readonly UsFederalHolidayCalendar Instance = new();

    private readonly ConcurrentDictionary<int, HashSet<DateOnly>> _observedByYear = new();

    private UsFederalHolidayCalendar() { }

    public bool IsHoliday(DateOnly date) =>
        _observedByYear.GetOrAdd(date.Year, ObservedDatesFallingIn).Contains(date);

    /// <summary>
    /// Every federal holiday whose observed date falls in <paramref name="year"/>,
    /// as (name, actual date, observed date), ordered by observed date.
    /// </summary>
    public static IReadOnlyList<FederalHoliday> HolidaysObservedIn(int year) =>
        HolidaysOf(year - 1).Concat(HolidaysOf(year)).Concat(HolidaysOf(year + 1))
            .Where(h => h.Observed.Year == year)
            .OrderBy(h => h.Observed)
            .ToList();

    private static HashSet<DateOnly> ObservedDatesFallingIn(int year) =>
        HolidaysObservedIn(year).Select(h => h.Observed).ToHashSet();

    private static IEnumerable<FederalHoliday> HolidaysOf(int year)
    {
        if (year < DateOnly.MinValue.Year + 1 || year >= DateOnly.MaxValue.Year) yield break;

        yield return Fixed("New Year's Day", year, 1, 1);
        if (year >= 1986)
            yield return Floating("Birthday of Martin Luther King, Jr.", NthWeekday(year, 1, DayOfWeek.Monday, 3));
        yield return Floating("Washington's Birthday", NthWeekday(year, 2, DayOfWeek.Monday, 3));
        yield return Floating("Memorial Day", LastWeekday(year, 5, DayOfWeek.Monday));
        if (year >= 2021)
            yield return Fixed("Juneteenth National Independence Day", year, 6, 19);
        yield return Fixed("Independence Day", year, 7, 4);
        yield return Floating("Labor Day", NthWeekday(year, 9, DayOfWeek.Monday, 1));
        yield return Floating("Columbus Day", NthWeekday(year, 10, DayOfWeek.Monday, 2));
        yield return Fixed("Veterans Day", year, 11, 11);
        yield return Floating("Thanksgiving Day", NthWeekday(year, 11, DayOfWeek.Thursday, 4));
        yield return Fixed("Christmas Day", year, 12, 25);
    }

    private static FederalHoliday Fixed(string name, int year, int month, int day)
    {
        var actual = new DateOnly(year, month, day);
        return new FederalHoliday(name, actual, ObservedDate(actual));
    }

    private static FederalHoliday Floating(string name, DateOnly date) => new(name, date, date);

    /// <summary>Saturday → preceding Friday, Sunday → following Monday, otherwise unchanged.</summary>
    public static DateOnly ObservedDate(DateOnly actual) => actual.DayOfWeek switch
    {
        DayOfWeek.Saturday => actual.AddDays(-1),
        DayOfWeek.Sunday => actual.AddDays(1),
        _ => actual
    };

    private static DateOnly NthWeekday(int year, int month, DayOfWeek weekday, int n)
    {
        var first = new DateOnly(year, month, 1);
        var offset = ((int)weekday - (int)first.DayOfWeek + 7) % 7;
        return first.AddDays(offset + 7 * (n - 1));
    }

    private static DateOnly LastWeekday(int year, int month, DayOfWeek weekday)
    {
        var last = new DateOnly(year, month, DateTime.DaysInMonth(year, month));
        var offset = ((int)last.DayOfWeek - (int)weekday + 7) % 7;
        return last.AddDays(-offset);
    }
}

/// <summary>A federal holiday and the date it is observed.</summary>
public sealed record FederalHoliday(string Name, DateOnly Actual, DateOnly Observed);

/// <summary>
/// A base calendar (normally <see cref="UsFederalHolidayCalendar"/>) plus
/// configured additions: one-off dates and dates that recur every year
/// (shifted off a weekend the federal way).
/// </summary>
public sealed class CompositeHolidayCalendar : IHolidayCalendar
{
    private readonly IHolidayCalendar? _base;
    private readonly HashSet<DateOnly> _dates;
    private readonly HashSet<(int Month, int Day)> _annual;

    public CompositeHolidayCalendar(
        IHolidayCalendar? baseCalendar,
        IEnumerable<DateOnly> dates,
        IEnumerable<(int Month, int Day)> annual)
    {
        _base = baseCalendar;
        _dates = dates.ToHashSet();
        _annual = annual.ToHashSet();
    }

    public bool IsHoliday(DateOnly date)
    {
        if (_dates.Contains(date)) return true;
        if (_base?.IsHoliday(date) == true) return true;
        if (_annual.Count == 0) return false;

        // An annual holiday is observed on date when it falls on date
        // itself (a weekday), on the following Saturday (date is Friday) or
        // the preceding Sunday (date is Monday). The holiday's actual date
        // may sit in the neighbouring year (Dec 31 / Jan 1).
        foreach (var candidate in new[] { date, date.AddDays(1), date.AddDays(-1) })
        {
            if (_annual.Contains((candidate.Month, candidate.Day))
                && UsFederalHolidayCalendar.ObservedDate(candidate) == date)
            {
                return true;
            }
        }
        return false;
    }
}

/// <summary>
/// <c>AppealHolidays</c> configuration. Every tenant gets the U.S. federal
/// holidays (unless <see cref="IncludeUsFederalHolidays"/> is false) plus
/// <see cref="AdditionalHolidays"/>; a tenant listed under
/// <see cref="Tenants"/> also gets the holidays of each of its
/// <see cref="AppealTenantHolidayOptions.States"/> (from
/// <see cref="StateHolidays"/>) and its own additions.
///
/// A holiday entry is either <c>yyyy-MM-dd</c> (that date only, used as
/// given) or <c>MM-dd</c> (every year, observed on the Friday before /
/// Monday after when it falls on a weekend).
/// </summary>
/// <example>
/// <code>
/// "AppealHolidays": {
///   "StateHolidays": { "TX": [ "2026-11-27", "03-02" ] },
///   "Tenants": { "tenant-a": { "States": [ "TX" ], "AdditionalHolidays": [ "2026-12-24" ] } }
/// }
/// </code>
/// </example>
public sealed class AppealHolidayOptions
{
    public const string SectionName = "AppealHolidays";

    public bool IncludeUsFederalHolidays { get; set; } = true;

    public List<string> AdditionalHolidays { get; set; } = new();

    /// <summary>State (or other jurisdiction) code → holiday entries.</summary>
    public Dictionary<string, List<string>> StateHolidays { get; set; } = new();

    /// <summary>Tenant id → that tenant's states and additions.</summary>
    public Dictionary<string, AppealTenantHolidayOptions> Tenants { get; set; } = new();
}

public sealed class AppealTenantHolidayOptions
{
    /// <summary>Keys into <see cref="AppealHolidayOptions.StateHolidays"/>.</summary>
    public List<string> States { get; set; } = new();

    public List<string> AdditionalHolidays { get; set; } = new();
}

/// <summary>Resolves the holiday calendar that governs a tenant's working-day clocks.</summary>
public interface IAppealHolidayCalendarProvider
{
    IHolidayCalendar ForTenant(string tenantId);
}

/// <summary>
/// <see cref="IAppealHolidayCalendarProvider"/> over <see cref="AppealHolidayOptions"/>.
/// Every entry is validated when the provider is built, so a malformed
/// date or an unknown state fails startup instead of silently shortening
/// a regulatory clock.
/// </summary>
public sealed class ConfiguredAppealHolidayCalendarProvider : IAppealHolidayCalendarProvider
{
    private readonly IHolidayCalendar _default;
    private readonly Dictionary<string, IHolidayCalendar> _tenants = new(StringComparer.Ordinal);

    public ConfiguredAppealHolidayCalendarProvider(AppealHolidayOptions options)
    {
        var baseCalendar = options.IncludeUsFederalHolidays ? UsFederalHolidayCalendar.Instance : null;
        var states = new Dictionary<string, List<string>>(options.StateHolidays, StringComparer.OrdinalIgnoreCase);

        _default = Build(baseCalendar, options.AdditionalHolidays, "AdditionalHolidays");

        foreach (var (tenantId, tenant) in options.Tenants)
        {
            var entries = new List<(string Entry, string Source)>();
            entries.AddRange(options.AdditionalHolidays.Select(e => (e, "AdditionalHolidays")));
            foreach (var state in tenant.States)
            {
                if (!states.TryGetValue(state, out var stateEntries))
                {
                    throw new InvalidOperationException(
                        $"{AppealHolidayOptions.SectionName}:Tenants:{tenantId}:States names '{state}', " +
                        $"which has no {AppealHolidayOptions.SectionName}:StateHolidays entry.");
                }
                entries.AddRange(stateEntries.Select(e => (e, $"StateHolidays:{state}")));
            }
            entries.AddRange(tenant.AdditionalHolidays.Select(e => (e, $"Tenants:{tenantId}:AdditionalHolidays")));
            _tenants[tenantId] = Build(baseCalendar, entries);
        }

        // Validate state lists no tenant references yet, too.
        foreach (var (state, stateEntries) in states)
            Build(null, stateEntries, $"StateHolidays:{state}");
    }

    public IHolidayCalendar ForTenant(string tenantId) =>
        _tenants.TryGetValue(tenantId, out var calendar) ? calendar : _default;

    private static IHolidayCalendar Build(IHolidayCalendar? baseCalendar, IEnumerable<string> entries, string source) =>
        Build(baseCalendar, entries.Select(e => (e, source)));

    private static IHolidayCalendar Build(IHolidayCalendar? baseCalendar, IEnumerable<(string Entry, string Source)> entries)
    {
        var dates = new List<DateOnly>();
        var annual = new List<(int, int)>();
        foreach (var (entry, source) in entries)
        {
            var text = entry?.Trim() ?? string.Empty;
            if (DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            {
                dates.Add(date);
            }
            else if (DateOnly.TryParseExact("2000-" + text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var md))
            {
                // 2000 is a leap year, so 02-29 parses; it then only matches in leap years.
                annual.Add((md.Month, md.Day));
            }
            else
            {
                throw new InvalidOperationException(
                    $"{AppealHolidayOptions.SectionName}:{source} entry '{entry}' is not yyyy-MM-dd or MM-dd.");
            }
        }

        return dates.Count == 0 && annual.Count == 0 && baseCalendar is not null
            ? baseCalendar
            : new CompositeHolidayCalendar(baseCalendar, dates, annual);
    }
}
