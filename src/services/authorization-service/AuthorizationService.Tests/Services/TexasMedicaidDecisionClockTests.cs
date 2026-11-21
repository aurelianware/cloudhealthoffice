using AuthorizationService.Models;
using AuthorizationService.Services;
using AuthorizationService.Services.StatePrograms;
using CloudHealthOffice.Infrastructure.Programs;

namespace AuthorizationService.Tests.Services;

/// <summary>
/// TX-05: Texas Medicaid decision clocks (Tex. Gov't Code §§540.0302–540.0303)
/// on top of the federal receipt clock. Times are Central (America/Chicago).
/// </summary>
public class TexasMedicaidDecisionClockTests
{
    private const string Tenant = "pchp";

    private static readonly TimeZoneInfo Central = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");

    private static TexasMedicaidDecisionClockOptions Options(params DateOnly[] holidays) => new()
    {
        Tenants = [Tenant],
        Holidays = [.. holidays],
    };

    private static DateTime CentralToUtc(int year, int month, int day, int hour, int minute = 0)
        => TimeZoneInfo.ConvertTimeToUtc(new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified), Central);

    private static Authorization Request(
        DateTime receivedUtc,
        PaDecisionScenario scenario = PaDecisionScenario.Unspecified,
        LineOfBusiness lob = LineOfBusiness.Medicaid,
        string? urgency = null,
        AuthorizationType type = AuthorizationType.PreAuthorization,
        string tenant = Tenant)
    {
        var auth = new Authorization
        {
            TenantId = tenant,
            SubmittedDate = receivedUtc,
            LineOfBusiness = lob,
            LevelOfService = urgency,
            DecisionScenario = scenario,
            AuthorizationType = type,
        };
        AuthorizationDeadlinePolicy.Initialize(auth);
        return auth;
    }

    // The ledger's reproduction: nonurgent, received Monday October 19, 2026.
    // The federal clock says October 26; three business days is Thursday the 22nd.
    [Fact]
    public void NotHospitalized_IsThreeBusinessDays_NotSevenCalendarDays()
    {
        var auth = Request(CentralToUtc(2026, 10, 19, 10), PaDecisionScenario.NotHospitalized);

        TexasMedicaidDecisionClock.Apply(auth, Options()).Should().BeTrue();

        auth.DecisionDueAt.Should().Be(CentralToUtc(2026, 10, 22, 10));
        auth.DecisionDeadlinePolicy.Should().Be("cho-receipt-v1+tx-gc-540-v1:NotHospitalized");
    }

    [Fact]
    public void Hospitalized_IsOneBusinessDay_SkippingTheWeekend()
    {
        var auth = Request(CentralToUtc(2026, 10, 23, 16), PaDecisionScenario.Hospitalized); // Friday

        TexasMedicaidDecisionClock.Apply(auth, Options());

        auth.DecisionDueAt.Should().Be(CentralToUtc(2026, 10, 26, 16)); // Monday
    }

    [Fact]
    public void InpatientDischarge_IsSeventyTwoHours()
    {
        var received = CentralToUtc(2026, 10, 23, 16);
        var auth = Request(received, PaDecisionScenario.InpatientDischarge);

        TexasMedicaidDecisionClock.Apply(auth, Options());

        auth.DecisionDueAt.Should().Be(received.AddHours(72));
    }

    [Fact]
    public void Poststabilization_IsOneHour()
    {
        var received = CentralToUtc(2026, 10, 24, 2, 15); // a Saturday night: hours do not wait for business days
        var auth = Request(received, PaDecisionScenario.PoststabilizationOrLifeThreatening, urgency: "U");

        TexasMedicaidDecisionClock.Apply(auth, Options());

        auth.DecisionDueAt.Should().Be(received.AddHours(1));
    }

    [Fact]
    public void ReceivedOnAWeekend_CountsFromTheNextBusinessDay()
    {
        var auth = Request(CentralToUtc(2026, 10, 24, 11), PaDecisionScenario.NotHospitalized); // Saturday

        TexasMedicaidDecisionClock.Apply(auth, Options());

        auth.DecisionDueAt.Should().Be(CentralToUtc(2026, 10, 29, 0)); // Thursday, from Monday's opening
    }

    [Fact]
    public void ConfiguredHolidays_AreNotBusinessDays()
    {
        // Wednesday Nov 25, 2026; Thanksgiving Thu 26 and Fri 27 configured as holidays.
        var auth = Request(CentralToUtc(2026, 11, 25, 9), PaDecisionScenario.Hospitalized);

        TexasMedicaidDecisionClock.Apply(auth, Options(new DateOnly(2026, 11, 26), new DateOnly(2026, 11, 27)));

        auth.DecisionDueAt.Should().Be(CentralToUtc(2026, 11, 30, 9));
    }

    [Fact]
    public void AcrossTheFallDstChange_KeepsTheLocalTimeOfDay()
    {
        // Friday Oct 30, 2026 10:00 CDT; DST ends Sunday Nov 1. Due Wed Nov 4 10:00 CST.
        var auth = Request(CentralToUtc(2026, 10, 30, 10), PaDecisionScenario.NotHospitalized);

        TexasMedicaidDecisionClock.Apply(auth, Options());

        auth.DecisionDueAt.Should().Be(CentralToUtc(2026, 11, 4, 10));
    }

    [Fact]
    public void TheFederalClockStillWins_WhenItIsEarlier()
    {
        // Expedited, nonhospitalized, received Friday before a configured
        // holiday Monday: three business days runs to Thursday, the federal
        // 72 hours runs to Monday. The earlier one stands.
        var received = CentralToUtc(2026, 10, 9, 15);
        var auth = Request(received, PaDecisionScenario.NotHospitalized, urgency: "U");

        TexasMedicaidDecisionClock.Apply(auth, Options(new DateOnly(2026, 10, 12)));

        auth.DecisionDueAt.Should().Be(received.AddHours(72));
    }

    [Fact]
    public void UnstatedScenario_ConcurrentReview_IsTakenAsHospitalized_AndMarkedInferred()
    {
        var auth = Request(CentralToUtc(2026, 10, 19, 10), type: AuthorizationType.ConcurrentReview);

        TexasMedicaidDecisionClock.Apply(auth, Options());

        auth.DecisionDueAt.Should().Be(CentralToUtc(2026, 10, 20, 10));
        auth.DecisionDeadlinePolicy.Should().EndWith(":Hospitalized+inferred");
    }

    [Fact]
    public void UnstatedScenario_OtherwiseIsTakenAsNotHospitalized()
    {
        var auth = Request(CentralToUtc(2026, 10, 19, 10));

        TexasMedicaidDecisionClock.Apply(auth, Options());

        auth.DecisionDueAt.Should().Be(CentralToUtc(2026, 10, 22, 10));
        auth.DecisionDeadlinePolicy.Should().EndWith(":NotHospitalized+inferred");
    }

    [Theory]
    [InlineData(LineOfBusiness.Commercial, Tenant)]
    [InlineData(LineOfBusiness.Medicare, Tenant)]
    [InlineData(LineOfBusiness.Medicaid, "another-plan")]
    public void OutsideAConfiguredTexasMedicaidTenant_NothingChanges(LineOfBusiness lob, string tenant)
    {
        var received = CentralToUtc(2026, 10, 19, 10);
        var auth = Request(received, PaDecisionScenario.Hospitalized, lob, tenant: tenant);

        TexasMedicaidDecisionClock.Apply(auth, Options()).Should().BeFalse();

        auth.DecisionDueAt.Should().Be(received.AddHours(168));
        auth.DecisionDeadlinePolicy.Should().Be(AuthorizationDeadlinePolicy.Version);
    }

    [Fact]
    public void NoTenantsConfigured_IsOffByDefault()
        => TexasMedicaidDecisionClock.Apply(
                Request(CentralToUtc(2026, 10, 19, 10), PaDecisionScenario.Hospitalized),
                new TexasMedicaidDecisionClockOptions())
            .Should().BeFalse();
}
