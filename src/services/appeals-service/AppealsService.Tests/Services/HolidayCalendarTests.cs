using AppealsService.Models;
using AppealsService.Services;

namespace AppealsService.Tests.Services;

public class HolidayCalendarTests
{
    private static DateOnly D(string iso) => DateOnly.Parse(iso, System.Globalization.CultureInfo.InvariantCulture);

    private static DateTime Utc(string iso) =>
        DateTime.Parse(iso, null, System.Globalization.DateTimeStyles.AdjustToUniversal);

    // ── U.S. federal holidays by rule ───────────────────────────────────

    public static IEnumerable<object[]> ObservedFederalHolidays() => new[]
    {
        new object[] { 2024, new[] { "2024-01-01", "2024-01-15", "2024-02-19", "2024-05-27", "2024-06-19", "2024-07-04",
                                      "2024-09-02", "2024-10-14", "2024-11-11", "2024-11-28", "2024-12-25" } },
        new object[] { 2025, new[] { "2025-01-01", "2025-01-20", "2025-02-17", "2025-05-26", "2025-06-19", "2025-07-04",
                                      "2025-09-01", "2025-10-13", "2025-11-11", "2025-11-27", "2025-12-25" } },
        // Independence Day on a Saturday → Friday July 3.
        new object[] { 2026, new[] { "2026-01-01", "2026-01-19", "2026-02-16", "2026-05-25", "2026-06-19", "2026-07-03",
                                      "2026-09-07", "2026-10-12", "2026-11-11", "2026-11-26", "2026-12-25" } },
        // Juneteenth Sat → Fri; Independence Day Sun → Mon; Christmas Sat → Fri;
        // New Year's Day 2028 (Sat) is observed Friday Dec 31, 2027 — twelve dates.
        new object[] { 2027, new[] { "2027-01-01", "2027-01-18", "2027-02-15", "2027-05-31", "2027-06-18", "2027-07-05",
                                      "2027-09-06", "2027-10-11", "2027-11-11", "2027-11-25", "2027-12-24", "2027-12-31" } },
        // ...so 2028 observes only ten; Veterans Day Sat → Fri Nov 10.
        new object[] { 2028, new[] { "2028-01-17", "2028-02-21", "2028-05-29", "2028-06-19", "2028-07-04",
                                      "2028-09-04", "2028-10-09", "2028-11-10", "2028-11-23", "2028-12-25" } },
    };

    [Theory]
    [MemberData(nameof(ObservedFederalHolidays))]
    public void Federal_Holidays_Are_Computed_By_Rule_For_Any_Year(int year, string[] expected)
    {
        UsFederalHolidayCalendar.HolidaysObservedIn(year).Select(h => h.Observed)
            .Should().Equal(expected.Select(D));

        var calendar = UsFederalHolidayCalendar.Instance;
        var all = Enumerable.Range(0, DateTime.IsLeapYear(year) ? 366 : 365)
            .Select(i => new DateOnly(year, 1, 1).AddDays(i))
            .Where(calendar.IsHoliday);
        all.Should().Equal(expected.Select(D));
    }

    [Theory]
    [InlineData("2026-07-04", "2026-07-03")] // Saturday → Friday
    [InlineData("2027-06-19", "2027-06-18")] // Saturday → Friday
    [InlineData("2022-06-19", "2022-06-20")] // Sunday → Monday
    [InlineData("2022-12-25", "2022-12-26")] // Sunday → Monday
    [InlineData("2028-01-01", "2027-12-31")] // Saturday → Friday of the prior year
    [InlineData("2026-12-25", "2026-12-25")] // weekday → unchanged
    public void Weekend_Holidays_Shift_To_The_Observed_Weekday(string actual, string observed)
    {
        UsFederalHolidayCalendar.ObservedDate(D(actual)).Should().Be(D(observed));
        UsFederalHolidayCalendar.Instance.IsHoliday(D(observed)).Should().BeTrue();
        if (actual != observed) UsFederalHolidayCalendar.Instance.IsHoliday(D(actual)).Should().BeFalse();
    }

    [Fact]
    public void Holidays_Start_In_The_Year_They_Were_First_Observed()
    {
        UsFederalHolidayCalendar.Instance.IsHoliday(D("2020-06-19")).Should().BeFalse();
        UsFederalHolidayCalendar.Instance.IsHoliday(D("2021-06-18")).Should().BeTrue(); // 2021-06-19 was a Saturday
        UsFederalHolidayCalendar.Instance.IsHoliday(D("1985-01-21")).Should().BeFalse();
        UsFederalHolidayCalendar.Instance.IsHoliday(D("1986-01-20")).Should().BeTrue();
    }

    // ── Working-day arithmetic ──────────────────────────────────────────

    [Fact]
    public void Fair_Hearing_Received_The_Day_Before_Thanksgiving_Skips_The_Holiday()
    {
        // Wed 2026-11-25 → Thu Thanksgiving (holiday), Fri (1), Mon (2), Tue (3).
        AppealResponseDeadlinePolicy.ComputeEnforceableMaximum(
                Utc("2026-11-25T15:00:00Z"), LineOfBusiness.Medicaid, AppealType.Reconsideration,
                AppealLevel.ExternalReview, isUrgent: true)
            .Should().Be(Utc("2026-12-01T15:00:00Z"));
    }

    [Fact]
    public void Tenant_Added_State_Holiday_Extends_The_Working_Day_Clock()
    {
        var provider = new ConfiguredAppealHolidayCalendarProvider(new AppealHolidayOptions
        {
            StateHolidays = new() { ["TX"] = new() { "2026-11-27" } }, // day after Thanksgiving
            Tenants = new() { ["tenant-tx"] = new() { States = new() { "tx" } } }
        });

        var received = Utc("2026-11-25T15:00:00Z");
        // Thu and Fri are both holidays for the Texas tenant: Mon (1), Tue (2), Wed (3).
        AppealResponseDeadlinePolicy.ComputeEnforceableMaximum(
                received, LineOfBusiness.Medicaid, AppealType.Reconsideration,
                AppealLevel.ExternalReview, isUrgent: true, provider.ForTenant("tenant-tx"))
            .Should().Be(Utc("2026-12-02T15:00:00Z"));

        // Other tenants keep the federal calendar only.
        AppealResponseDeadlinePolicy.ComputeEnforceableMaximum(
                received, LineOfBusiness.Medicaid, AppealType.Reconsideration,
                AppealLevel.ExternalReview, isUrgent: true, provider.ForTenant("tenant-other"))
            .Should().Be(Utc("2026-12-01T15:00:00Z"));

        // The extension ceiling uses the same calendar.
        AppealResponseDeadlinePolicy.ComputeMaxExtendedTargetResponseDate(
                received, LineOfBusiness.Medicaid, AppealType.Reconsideration,
                AppealLevel.ExternalReview, isUrgent: true, provider.ForTenant("tenant-tx"))
            .Should().Be(Utc("2026-12-02T15:00:00Z"));
    }

    [Fact]
    public void Configured_Additions_Keep_Federal_Holidays_And_Support_Annual_Entries()
    {
        var provider = new ConfiguredAppealHolidayCalendarProvider(new AppealHolidayOptions
        {
            AdditionalHolidays = new() { "03-02" }, // every year, weekend-shifted
            Tenants = new() { ["t1"] = new() { AdditionalHolidays = new() { "2026-12-24" } } }
        });

        var t1 = provider.ForTenant("t1");
        t1.IsHoliday(D("2026-12-25")).Should().BeTrue("federal holidays stay");
        t1.IsHoliday(D("2026-12-24")).Should().BeTrue();
        t1.IsHoliday(D("2026-03-02")).Should().BeTrue();       // Monday
        t1.IsHoliday(D("2025-03-03")).Should().BeTrue();       // 2025-03-02 is a Sunday → Monday
        t1.IsHoliday(D("2025-03-02")).Should().BeFalse();
        provider.ForTenant("t2").IsHoliday(D("2026-12-24")).Should().BeFalse();
        provider.ForTenant("t2").IsHoliday(D("2026-03-02")).Should().BeTrue();
    }

    [Fact]
    public void Federal_Holidays_Can_Be_Turned_Off()
    {
        var provider = new ConfiguredAppealHolidayCalendarProvider(new AppealHolidayOptions { IncludeUsFederalHolidays = false });
        provider.ForTenant("t1").IsHoliday(D("2026-11-26")).Should().BeFalse();
    }

    [Theory]
    [InlineData("2026-13-01")]
    [InlineData("11/27/2026")]
    [InlineData("")]
    public void Malformed_Entries_Fail_At_Construction(string entry)
    {
        var act = () => new ConfiguredAppealHolidayCalendarProvider(new AppealHolidayOptions
        {
            AdditionalHolidays = new() { entry }
        });
        act.Should().Throw<InvalidOperationException>().WithMessage("*AppealHolidays*");
    }

    [Fact]
    public void A_Tenant_Naming_An_Unknown_State_Fails_At_Construction()
    {
        var act = () => new ConfiguredAppealHolidayCalendarProvider(new AppealHolidayOptions
        {
            Tenants = new() { ["t1"] = new() { States = new() { "ZZ" } } }
        });
        act.Should().Throw<InvalidOperationException>().WithMessage("*ZZ*");
    }
}
