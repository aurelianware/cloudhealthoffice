using System.Globalization;
using System.Text.Json.Serialization;

namespace CloudHealthOffice.Infrastructure.Edi.Core;

/// <summary>
/// Configuration section <c>CoreConnectivity:Availability</c>: the downtime
/// CHO publishes under the CAQH CORE system availability rule. Regular
/// downtime repeats every week; other planned downtime is announced at least
/// a week ahead; unscheduled and emergency downtime is recorded once it
/// happens, so the weekly figure covers every hour the system was down.
/// </summary>
public sealed class CoreAvailabilityOptions
{
    /// <summary>Weekly regular downtime (UTC).</summary>
    public List<CoreRegularDowntime> Regular { get; set; } = [];

    /// <summary>One-off windows: planned (non-routine), emergency and unscheduled downtime.</summary>
    public List<CoreDowntimeWindow> Windows { get; set; } = [];

    /// <summary>How far ahead the published schedule lists windows.</summary>
    public TimeSpan PublishHorizon { get; set; } = TimeSpan.FromDays(30);
}

/// <summary>Downtime that repeats every week, e.g. Sunday 06:00 UTC for 2 hours.</summary>
public sealed class CoreRegularDowntime
{
    [JsonConverter(typeof(JsonStringEnumConverter<DayOfWeek>))]
    public DayOfWeek Day { get; set; } = DayOfWeek.Sunday;

    /// <summary>Start time of day, UTC.</summary>
    public TimeSpan Start { get; set; }

    public TimeSpan Duration { get; set; }

    public string? Reason { get; set; }
}

public enum CoreDowntimeKind
{
    /// <summary>Non-routine downtime announced ahead (the rule asks for a week's notice).</summary>
    Planned,

    /// <summary>Downtime for an emergency fix, announced as soon as it is decided.</summary>
    Emergency,

    /// <summary>An outage, recorded once it happens.</summary>
    Unscheduled,
}

/// <summary>A one-off downtime window.</summary>
public sealed class CoreDowntimeWindow
{
    public CoreDowntimeKind Kind { get; set; } = CoreDowntimeKind.Planned;

    public DateTimeOffset Start { get; set; }

    /// <summary>Unset while an unscheduled outage is still going on.</summary>
    public DateTimeOffset? End { get; set; }

    /// <summary>When the window was announced to trading partners.</summary>
    public DateTimeOffset? AnnouncedAt { get; set; }

    public string? Reason { get; set; }
}

/// <summary>A concrete downtime interval, regular or one-off.</summary>
public sealed record CoreDowntimeInterval(string Kind, DateTimeOffset Start, DateTimeOffset? End, string? Reason)
{
    /// <summary>A planned window announced less than a week before it starts.</summary>
    public bool ShortNotice { get; init; }
}

/// <summary>Availability for one calendar week (Sunday 00:00 UTC to the next Sunday).</summary>
public sealed record CoreWeeklyAvailability(
    DateTimeOffset WeekStart,
    double DowntimeHours,
    double ScheduledDowntimeHours,
    double UnscheduledDowntimeHours,
    double AvailabilityPercent,
    bool MeetsRule);

/// <summary>
/// The published schedule and the weekly figure, worked out from
/// <see cref="CoreAvailabilityOptions"/>. The rule asks for 86% availability
/// in each calendar week, counting every kind of downtime.
/// </summary>
public static class CoreAvailabilitySchedule
{
    public const double RequiredPercent = 86.0;
    public static readonly TimeSpan MinimumNotice = TimeSpan.FromDays(7);
    private static readonly TimeSpan Week = TimeSpan.FromDays(7);

    /// <summary>The downtime interval covering <paramref name="now"/>, if any.</summary>
    public static CoreDowntimeInterval? Current(CoreAvailabilityOptions options, DateTimeOffset now) =>
        Intervals(options, now - Week, now + TimeSpan.FromTicks(1))
            .FirstOrDefault(i => i.Start <= now && (i.End is null || i.End > now));

    /// <summary>Downtime intervals that overlap [<paramref name="from"/>, <paramref name="to"/>), in start order.</summary>
    public static IReadOnlyList<CoreDowntimeInterval> Intervals(CoreAvailabilityOptions options, DateTimeOffset from, DateTimeOffset to)
    {
        var result = new List<CoreDowntimeInterval>();

        foreach (var regular in options.Regular.Where(r => r.Duration > TimeSpan.Zero))
        {
            // Every occurrence that could overlap: start one week before `from`.
            for (var day = from.UtcDateTime.Date.AddDays(-7); day < to.UtcDateTime; day = day.AddDays(1))
            {
                if (day.DayOfWeek != regular.Day) continue;
                var start = new DateTimeOffset(day + regular.Start, TimeSpan.Zero);
                var end = start + regular.Duration;
                if (end > from && start < to)
                    result.Add(new CoreDowntimeInterval("regular", start, end, regular.Reason));
            }
        }

        foreach (var window in options.Windows)
        {
            // Only an unscheduled outage may still be open; any other window without an end is a config error (see Problems).
            if (window.End is null && window.Kind != CoreDowntimeKind.Unscheduled) continue;
            if (window.Start >= to || window.End <= from) continue;
            result.Add(new CoreDowntimeInterval(KindName(window.Kind), window.Start, window.End, window.Reason)
            {
                ShortNotice = window.Kind == CoreDowntimeKind.Planned
                    && (window.AnnouncedAt is null || window.Start - window.AnnouncedAt.Value < MinimumNotice),
            });
        }

        return result.OrderBy(i => i.Start).ToList();
    }

    /// <summary>The calendar week (Sunday 00:00 UTC) containing <paramref name="instant"/>.</summary>
    public static DateTimeOffset WeekStart(DateTimeOffset instant)
    {
        var day = instant.UtcDateTime.Date;
        return new DateTimeOffset(day.AddDays(-(int)day.DayOfWeek), TimeSpan.Zero);
    }

    /// <summary>
    /// Availability for the week starting <paramref name="weekStart"/>. An
    /// unscheduled outage with no end counts up to <paramref name="now"/>;
    /// a week still in progress counts downtime so far against the whole week.
    /// Overlapping windows are counted once.
    /// </summary>
    public static CoreWeeklyAvailability ForWeek(CoreAvailabilityOptions options, DateTimeOffset weekStart, DateTimeOffset now)
    {
        var weekEnd = weekStart + Week;
        var intervals = Intervals(options, weekStart, weekEnd)
            .Select(i => (i.Kind,
                Start: Max(i.Start, weekStart),
                End: Min(i.End ?? Min(now, weekEnd), weekEnd)))
            .Where(i => i.End > i.Start)
            .ToList();

        var total = Union(intervals.Select(i => (i.Start, i.End)));
        var unscheduled = Union(intervals.Where(i => i.Kind is "unscheduled" or "emergency").Select(i => (i.Start, i.End)));
        var scheduled = total - unscheduled;
        var percent = Math.Round(100.0 * (1 - total.TotalHours / Week.TotalHours), 2);

        return new CoreWeeklyAvailability(weekStart,
            Math.Round(total.TotalHours, 2), Math.Round(scheduled.TotalHours, 2), Math.Round(unscheduled.TotalHours, 2),
            percent, percent >= RequiredPercent);
    }

    /// <summary>
    /// Problems with the schedule itself: regular downtime alone that breaks
    /// the 86% rule, planned windows without a week's notice, windows that
    /// end before they start.
    /// </summary>
    public static IReadOnlyList<string> Problems(CoreAvailabilityOptions options)
    {
        var problems = new List<string>();
        var regularHours = options.Regular.Sum(r => Math.Max(0, r.Duration.TotalHours));
        if (100.0 * (1 - regularHours / Week.TotalHours) < RequiredPercent)
            problems.Add(string.Create(CultureInfo.InvariantCulture,
                $"Regular downtime of {regularHours:0.#} hours a week leaves less than {RequiredPercent}% availability."));
        foreach (var w in options.Windows)
        {
            if (w.End is { } end && end <= w.Start)
                problems.Add($"Downtime window starting {w.Start:u} ends before it starts.");
            if (w.Kind == CoreDowntimeKind.Planned && (w.AnnouncedAt is null || w.Start - w.AnnouncedAt.Value < MinimumNotice))
                problems.Add($"Planned downtime starting {w.Start:u} was announced less than a week ahead.");
            if (w.Kind != CoreDowntimeKind.Unscheduled && w.End is null)
                problems.Add($"{w.Kind} downtime starting {w.Start:u} has no end.");
        }
        return problems;
    }

    private static string KindName(CoreDowntimeKind kind) => kind switch
    {
        CoreDowntimeKind.Emergency => "emergency",
        CoreDowntimeKind.Unscheduled => "unscheduled",
        _ => "planned",
    };

    private static TimeSpan Union(IEnumerable<(DateTimeOffset Start, DateTimeOffset End)> intervals)
    {
        var total = TimeSpan.Zero;
        DateTimeOffset? runStart = null, runEnd = null;
        foreach (var (start, end) in intervals.OrderBy(i => i.Start))
        {
            if (runEnd is null || start > runEnd)
            {
                if (runEnd is not null) total += runEnd.Value - runStart!.Value;
                (runStart, runEnd) = (start, end);
            }
            else if (end > runEnd)
            {
                runEnd = end;
            }
        }
        if (runEnd is not null) total += runEnd.Value - runStart!.Value;
        return total;
    }

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;
    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;
}

/// <summary>The body of <c>GET {endpoint}/availability</c>.</summary>
public sealed record CoreAvailabilityReport(
    DateTimeOffset GeneratedAt,
    string Status,
    CoreDowntimeInterval? CurrentDowntime,
    IReadOnlyList<CoreRegularDowntime> RegularDowntime,
    IReadOnlyList<CoreDowntimeInterval> UpcomingDowntime,
    CoreWeeklyAvailability ThisWeek,
    CoreWeeklyAvailability LastWeek,
    double RequiredPercent)
{
    public static CoreAvailabilityReport Build(CoreAvailabilityOptions options, DateTimeOffset now)
    {
        var current = CoreAvailabilitySchedule.Current(options, now);
        var weekStart = CoreAvailabilitySchedule.WeekStart(now);
        var upcoming = CoreAvailabilitySchedule.Intervals(options, now, now + options.PublishHorizon)
            .Where(i => i.Kind != "unscheduled" && i.Start > now)
            .ToList();
        return new CoreAvailabilityReport(
            now,
            current is null ? "available" : "downtime",
            current,
            options.Regular,
            upcoming,
            CoreAvailabilitySchedule.ForWeek(options, weekStart, now),
            CoreAvailabilitySchedule.ForWeek(options, weekStart.AddDays(-7), now),
            CoreAvailabilitySchedule.RequiredPercent);
    }
}
