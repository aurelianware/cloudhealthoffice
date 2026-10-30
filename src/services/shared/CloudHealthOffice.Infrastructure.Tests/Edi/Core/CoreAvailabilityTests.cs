using CloudHealthOffice.Infrastructure.Edi.Core;
using Xunit;

namespace CloudHealthOffice.Infrastructure.Tests.Edi.Core;

/// <summary>
/// CAQH CORE system availability: the published downtime schedule, the
/// downtime in force now, and availability per calendar week (86% required).
/// </summary>
public class CoreAvailabilityTests
{
    // Sunday 2026-11-01 00:00 UTC starts the week.
    private static readonly DateTimeOffset Week = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    private static CoreAvailabilityOptions SundayMaintenance(params CoreDowntimeWindow[] windows) => new()
    {
        Regular = [new CoreRegularDowntime { Day = DayOfWeek.Sunday, Start = TimeSpan.FromHours(6), Duration = TimeSpan.FromHours(2), Reason = "Weekly maintenance" }],
        Windows = windows.ToList(),
    };

    [Fact]
    public void WeekStart_IsTheSundayBefore()
    {
        Assert.Equal(Week, CoreAvailabilitySchedule.WeekStart(new DateTimeOffset(2026, 11, 4, 15, 30, 0, TimeSpan.Zero)));
        Assert.Equal(Week, CoreAvailabilitySchedule.WeekStart(Week));
    }

    [Fact]
    public void RegularDowntime_IsCurrent_OnlyInsideItsWindow()
    {
        var options = SundayMaintenance();

        Assert.Equal("regular", CoreAvailabilitySchedule.Current(options, Week.AddHours(7))?.Kind);
        Assert.Null(CoreAvailabilitySchedule.Current(options, Week.AddHours(8)));
        Assert.Null(CoreAvailabilitySchedule.Current(options, Week.AddDays(1).AddHours(7)));
    }

    [Fact]
    public void AnOngoingOutage_IsCurrent_UntilItIsGivenAnEnd()
    {
        var outage = new CoreDowntimeWindow { Kind = CoreDowntimeKind.Unscheduled, Start = Week.AddDays(2) };
        var options = SundayMaintenance(outage);

        Assert.Equal("unscheduled", CoreAvailabilitySchedule.Current(options, Week.AddDays(3))?.Kind);
        outage.End = Week.AddDays(2).AddHours(4);
        Assert.Null(CoreAvailabilitySchedule.Current(options, Week.AddDays(3)));
    }

    [Fact]
    public void WeeklyAvailability_CountsEveryKindOfDowntime_AndOverlapsOnce()
    {
        var options = SundayMaintenance(
            // Planned work running into the regular window: 05:00 to 07:00 overlaps 06:00 to 08:00.
            new CoreDowntimeWindow { Kind = CoreDowntimeKind.Planned, Start = Week.AddHours(5), End = Week.AddHours(7), AnnouncedAt = Week.AddDays(-10) },
            new CoreDowntimeWindow { Kind = CoreDowntimeKind.Unscheduled, Start = Week.AddDays(3), End = Week.AddDays(3).AddHours(5) });

        var week = CoreAvailabilitySchedule.ForWeek(options, Week, Week.AddDays(7));

        Assert.Equal(8, week.DowntimeHours);              // 05:00-08:00 on Sunday + 5 hours Wednesday
        Assert.Equal(5, week.UnscheduledDowntimeHours);
        Assert.Equal(3, week.ScheduledDowntimeHours);
        Assert.Equal(95.24, week.AvailabilityPercent);    // 160 of 168 hours
        Assert.True(week.MeetsRule);
    }

    [Fact]
    public void AWeekBelow86Percent_DoesNotMeetTheRule()
    {
        var options = SundayMaintenance(
            new CoreDowntimeWindow { Kind = CoreDowntimeKind.Unscheduled, Start = Week.AddDays(1), End = Week.AddDays(2) });

        var week = CoreAvailabilitySchedule.ForWeek(options, Week, Week.AddDays(7));

        Assert.Equal(26, week.DowntimeHours);
        Assert.False(week.MeetsRule);
    }

    [Fact]
    public void AnOutageAcrossTwoWeeks_CountsInEachWeekOnlyItsShare()
    {
        var options = new CoreAvailabilityOptions
        {
            Windows = [new CoreDowntimeWindow { Kind = CoreDowntimeKind.Unscheduled, Start = Week.AddHours(-3), End = Week.AddHours(2) }],
        };

        Assert.Equal(3, CoreAvailabilitySchedule.ForWeek(options, Week.AddDays(-7), Week.AddDays(7)).DowntimeHours);
        Assert.Equal(2, CoreAvailabilitySchedule.ForWeek(options, Week, Week.AddDays(7)).DowntimeHours);
    }

    [Fact]
    public void AnOngoingOutage_CountsUpToNow()
    {
        var options = new CoreAvailabilityOptions
        {
            Windows = [new CoreDowntimeWindow { Kind = CoreDowntimeKind.Unscheduled, Start = Week.AddDays(1) }],
        };

        Assert.Equal(6, CoreAvailabilitySchedule.ForWeek(options, Week, Week.AddDays(1).AddHours(6)).DowntimeHours);
    }

    [Fact]
    public void TheReport_ListsUpcomingDowntime_AndFlagsShortNotice()
    {
        var now = Week.AddDays(1);
        var options = SundayMaintenance(
            new CoreDowntimeWindow { Kind = CoreDowntimeKind.Planned, Start = now.AddDays(3), End = now.AddDays(3).AddHours(1), AnnouncedAt = now.AddDays(-1) },
            new CoreDowntimeWindow { Kind = CoreDowntimeKind.Unscheduled, Start = now.AddDays(-3), End = now.AddDays(-3).AddHours(1) });

        var report = CoreAvailabilityReport.Build(options, now);

        Assert.Equal("available", report.Status);
        var planned = Assert.Single(report.UpcomingDowntime, i => i.Kind == "planned");
        Assert.True(planned.ShortNotice);
        Assert.DoesNotContain(report.UpcomingDowntime, i => i.Kind == "unscheduled");
        // The next four or five Sunday maintenance windows within the 30-day horizon.
        Assert.InRange(report.UpcomingDowntime.Count(i => i.Kind == "regular"), 4, 5);
        Assert.Equal(Week, report.ThisWeek.WeekStart);
        Assert.Equal(Week.AddDays(-7), report.LastWeek.WeekStart);
    }

    [Fact]
    public void Problems_FlagTooMuchRegularDowntime_ShortNotice_AndOpenPlannedWindows()
    {
        var options = new CoreAvailabilityOptions
        {
            Regular = [new CoreRegularDowntime { Day = DayOfWeek.Saturday, Duration = TimeSpan.FromHours(30) }],
            Windows =
            [
                new CoreDowntimeWindow { Kind = CoreDowntimeKind.Planned, Start = Week, End = Week.AddHours(1) },
                new CoreDowntimeWindow { Kind = CoreDowntimeKind.Emergency, Start = Week },
            ],
        };

        var problems = CoreAvailabilitySchedule.Problems(options);

        Assert.Contains(problems, p => p.Contains("86%"));
        Assert.Contains(problems, p => p.Contains("less than a week ahead"));
        Assert.Contains(problems, p => p.Contains("has no end"));
        Assert.Empty(CoreAvailabilitySchedule.Problems(SundayMaintenance()));
    }
}
