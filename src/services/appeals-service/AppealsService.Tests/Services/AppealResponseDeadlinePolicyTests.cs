using AppealsService.Models;
using AppealsService.Services;

namespace AppealsService.Tests.Services;

public class AppealResponseDeadlinePolicyTests
{
    private static readonly DateTime Received = new(2026, 10, 8, 14, 30, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(LineOfBusiness.Medicare)]
    [InlineData(LineOfBusiness.Medicaid)]
    [InlineData(LineOfBusiness.Commercial)]
    [InlineData(LineOfBusiness.Marketplace)]
    public void Expedited_Appeal_Is_72_Hours_For_Every_LineOfBusiness(LineOfBusiness lob)
    {
        foreach (var type in new[] { AppealType.Reconsideration, AppealType.PeerReview, AppealType.ExternalReview })
        {
            AppealResponseDeadlinePolicy.ComputeTargetResponseDate(Received, lob, type, isUrgent: true)
                .Should().Be(Received.AddHours(72), $"{lob} {type} expedited");
        }
    }

    [Theory]
    [InlineData(LineOfBusiness.Medicare, 30)]    // 42 CFR 422.590(a) pre-service (conservative vs 60-day payment)
    [InlineData(LineOfBusiness.Medicaid, 30)]    // 42 CFR 438.408(b)(2)
    [InlineData(LineOfBusiness.Commercial, 30)]  // 29 CFR 2560.503-1(i)(2)(ii) pre-service
    [InlineData(LineOfBusiness.Marketplace, 30)] // 45 CFR 147.136
    [InlineData(LineOfBusiness.MedicarePartD, 7)] // 42 CFR 423.590(a)
    public void Standard_Appeal_Uses_Shortest_Applicable_Window(LineOfBusiness lob, int days)
    {
        AppealResponseDeadlinePolicy.ComputeTargetResponseDate(Received, lob, AppealType.Reconsideration, isUrgent: false)
            .Should().Be(Received.AddDays(days));
    }

    [Theory]
    [InlineData(LineOfBusiness.Medicare, false, 30 * 24)]    // 42 CFR 422.564(e)
    [InlineData(LineOfBusiness.Medicare, true, 24)]          // 42 CFR 422.564(f)
    [InlineData(LineOfBusiness.Medicaid, false, 90 * 24)]    // 42 CFR 438.408(b)(1)
    [InlineData(LineOfBusiness.Medicaid, true, 72)]
    [InlineData(LineOfBusiness.Commercial, false, 30 * 24)]
    [InlineData(LineOfBusiness.Commercial, true, 72)]
    public void Grievance_Uses_Grievance_Clock(LineOfBusiness lob, bool urgent, int hours)
    {
        AppealResponseDeadlinePolicy.ComputeTargetResponseDate(Received, lob, AppealType.Grievance, urgent)
            .Should().Be(Received.AddHours(hours));
    }

    [Fact]
    public void Urgent_Never_Exceeds_72_Hours_For_Any_Combination()
    {
        foreach (var lob in Enum.GetValues<LineOfBusiness>())
        foreach (var type in Enum.GetValues<AppealType>())
        {
            AppealResponseDeadlinePolicy.MaxResponseWindow(lob, type, isUrgent: true)
                .Should().BeLessThanOrEqualTo(TimeSpan.FromHours(72), $"{lob} {type}");
        }
    }

    // ── Part D ──────────────────────────────────────────────────────────

    [Fact]
    public void PartD_Redetermination_Is_7_Days_Standard_72_Hours_Expedited()
    {
        // 42 CFR 423.590(a), (d)
        AppealResponseDeadlinePolicy.ComputeTargetResponseDate(
                Received, LineOfBusiness.MedicarePartD, AppealType.Reconsideration, isUrgent: false)
            .Should().Be(Received.AddDays(7));
        AppealResponseDeadlinePolicy.ComputeTargetResponseDate(
                Received, LineOfBusiness.MedicarePartD, AppealType.Reconsideration, isUrgent: true)
            .Should().Be(Received.AddHours(72));
    }

    [Theory]
    [InlineData(false, 30 * 24)] // 42 CFR 423.564(e)
    [InlineData(true, 24)]       // 42 CFR 423.564(f)
    public void PartD_Grievance_Uses_Medicare_Grievance_Clock(bool urgent, int hours)
    {
        AppealResponseDeadlinePolicy.ComputeTargetResponseDate(
                Received, LineOfBusiness.MedicarePartD, AppealType.Grievance, urgent)
            .Should().Be(Received.AddHours(hours));
    }

    // ── External review ─────────────────────────────────────────────────

    [Theory]
    [InlineData(LineOfBusiness.Medicare, 30)]       // 42 CFR 422.592 — MA IRE
    [InlineData(LineOfBusiness.MedicarePartD, 7)]   // 42 CFR 423.600 — Part D IRE
    [InlineData(LineOfBusiness.Commercial, 45)]     // 45 CFR 147.136(d); 29 CFR 2590.715-2719
    [InlineData(LineOfBusiness.Marketplace, 45)]    // 45 CFR 147.136(d)
    [InlineData(LineOfBusiness.Medicaid, 90)]       // 42 CFR 431.244(f) — State Fair Hearing
    public void External_Review_Uses_Its_Own_Standard_Clock(LineOfBusiness lob, int days)
    {
        // Marked by the level...
        AppealResponseDeadlinePolicy.ComputeTargetResponseDate(
                Received, lob, AppealType.Reconsideration, AppealLevel.ExternalReview, isUrgent: false)
            .Should().Be(Received.AddDays(days), $"{lob} at ExternalReview level");
        // ...or by the type alone (e.g. a FHIR Task.code with no level extension).
        AppealResponseDeadlinePolicy.ComputeTargetResponseDate(
                Received, lob, AppealType.ExternalReview, AppealLevel.FirstLevel, isUrgent: false)
            .Should().Be(Received.AddDays(days), $"{lob} ExternalReview type");
    }

    [Theory]
    [InlineData(LineOfBusiness.Medicare)]
    [InlineData(LineOfBusiness.MedicarePartD)]
    [InlineData(LineOfBusiness.Commercial)]
    [InlineData(LineOfBusiness.Marketplace)]
    [InlineData(LineOfBusiness.Medicaid)]
    public void Expedited_External_Review_Is_72_Hours(LineOfBusiness lob)
    {
        AppealResponseDeadlinePolicy.ComputeTargetResponseDate(
                Received, lob, AppealType.Reconsideration, AppealLevel.ExternalReview, isUrgent: true)
            .Should().Be(Received.AddHours(72));
    }

    [Fact]
    public void Second_Level_Internal_Appeal_Stays_On_Internal_Clock()
    {
        AppealResponseDeadlinePolicy.ComputeTargetResponseDate(
                Received, LineOfBusiness.Commercial, AppealType.Reconsideration, AppealLevel.SecondLevel, isUrgent: false)
            .Should().Be(Received.AddDays(30));
    }

    [Fact]
    public void Grievance_Is_Never_External_Review()
    {
        AppealResponseDeadlinePolicy.IsExternalReview(AppealType.Grievance, AppealLevel.ExternalReview)
            .Should().BeFalse();
        AppealResponseDeadlinePolicy.ComputeTargetResponseDate(
                Received, LineOfBusiness.Commercial, AppealType.Grievance, AppealLevel.ExternalReview, isUrgent: false)
            .Should().Be(Received.AddDays(30), "grievance clock, not the 45-day external review clock");
    }

    [Fact]
    public void Medicaid_State_Fair_Hearing_Is_Not_Plan_Controlled()
    {
        AppealResponseDeadlinePolicy.IsPlanControlled(LineOfBusiness.Medicaid, AppealType.Reconsideration, AppealLevel.ExternalReview)
            .Should().BeFalse();
        AppealResponseDeadlinePolicy.IsPlanControlled(LineOfBusiness.Medicaid, AppealType.Reconsideration, AppealLevel.FirstLevel)
            .Should().BeTrue();
        AppealResponseDeadlinePolicy.IsPlanControlled(LineOfBusiness.Medicare, AppealType.ExternalReview, AppealLevel.ExternalReview)
            .Should().BeTrue("the MA IRE clock is tracked but the plan does not own it — IsPlanControlled flags only the State hearing");
    }

    // ── Extensions ──────────────────────────────────────────────────────

    [Theory]
    // Medicare Advantage appeals: 42 CFR 422.590(f) — standard AND expedited.
    [InlineData(LineOfBusiness.Medicare, AppealType.Reconsideration, AppealLevel.FirstLevel, false, true)]
    [InlineData(LineOfBusiness.Medicare, AppealType.Reconsideration, AppealLevel.FirstLevel, true, true)]
    [InlineData(LineOfBusiness.Medicare, AppealType.PeerReview, AppealLevel.SecondLevel, false, true)]
    // Medicaid managed care: 42 CFR 438.408(c) — standard AND expedited.
    [InlineData(LineOfBusiness.Medicaid, AppealType.Reconsideration, AppealLevel.FirstLevel, false, true)]
    [InlineData(LineOfBusiness.Medicaid, AppealType.Reconsideration, AppealLevel.FirstLevel, true, true)]
    [InlineData(LineOfBusiness.Medicaid, AppealType.Grievance, AppealLevel.FirstLevel, false, true)]
    // MA / Part D grievances: standard only (422.564(e)(2), 423.564(e)(2)).
    [InlineData(LineOfBusiness.Medicare, AppealType.Grievance, AppealLevel.FirstLevel, false, true)]
    [InlineData(LineOfBusiness.Medicare, AppealType.Grievance, AppealLevel.FirstLevel, true, false)]
    [InlineData(LineOfBusiness.MedicarePartD, AppealType.Grievance, AppealLevel.FirstLevel, false, true)]
    [InlineData(LineOfBusiness.MedicarePartD, AppealType.Grievance, AppealLevel.FirstLevel, true, false)]
    // Part D redeterminations: no extension (423.590).
    [InlineData(LineOfBusiness.MedicarePartD, AppealType.Reconsideration, AppealLevel.FirstLevel, false, false)]
    [InlineData(LineOfBusiness.MedicarePartD, AppealType.Reconsideration, AppealLevel.FirstLevel, true, false)]
    // Commercial / Marketplace: no unilateral extension (29 CFR 2560.503-1(i)).
    [InlineData(LineOfBusiness.Commercial, AppealType.Reconsideration, AppealLevel.FirstLevel, false, false)]
    [InlineData(LineOfBusiness.Marketplace, AppealType.Reconsideration, AppealLevel.FirstLevel, true, false)]
    [InlineData(LineOfBusiness.Commercial, AppealType.Grievance, AppealLevel.FirstLevel, false, false)]
    // External review / State Fair Hearing: never plan-extendable.
    [InlineData(LineOfBusiness.Medicare, AppealType.Reconsideration, AppealLevel.ExternalReview, false, false)]
    [InlineData(LineOfBusiness.Medicaid, AppealType.ExternalReview, AppealLevel.FirstLevel, false, false)]
    [InlineData(LineOfBusiness.Commercial, AppealType.ExternalReview, AppealLevel.ExternalReview, true, false)]
    public void Extension_Rule_Follows_Regulations(
        LineOfBusiness lob, AppealType type, AppealLevel level, bool urgent, bool permitted)
    {
        var rule = AppealResponseDeadlinePolicy.GetExtensionRule(lob, type, level, urgent);

        rule.IsPermitted.Should().Be(permitted);
        rule.MaxExtension.Should().Be(permitted ? TimeSpan.FromDays(14) : TimeSpan.Zero);
        rule.RegulatoryBasis.Should().NotBeNullOrWhiteSpace("the citation is recorded in the audit trail either way");
    }

    [Fact]
    public void Extension_Rule_Cites_The_Governing_Regulation()
    {
        AppealResponseDeadlinePolicy.GetExtensionRule(
                LineOfBusiness.Medicare, AppealType.Reconsideration, AppealLevel.FirstLevel, isUrgent: true)
            .RegulatoryBasis.Should().Contain("422.590(f)");
        AppealResponseDeadlinePolicy.GetExtensionRule(
                LineOfBusiness.Medicaid, AppealType.Reconsideration, AppealLevel.FirstLevel, isUrgent: false)
            .RegulatoryBasis.Should().Contain("438.408(c)");
    }

    [Fact]
    public void Max_Extended_Deadline_Is_Regulatory_Max_Plus_14_Days_When_Permitted()
    {
        AppealResponseDeadlinePolicy.ComputeMaxExtendedTargetResponseDate(
                Received, LineOfBusiness.Medicare, AppealType.Reconsideration, AppealLevel.FirstLevel, isUrgent: true)
            .Should().Be(Received.AddHours(72).AddDays(14));
        AppealResponseDeadlinePolicy.ComputeMaxExtendedTargetResponseDate(
                Received, LineOfBusiness.Commercial, AppealType.Reconsideration, AppealLevel.FirstLevel, isUrgent: false)
            .Should().Be(Received.AddDays(30), "no extension for commercial");
    }

    [Fact]
    public void Three_Argument_Overload_Matches_First_Level()
    {
        foreach (var lob in Enum.GetValues<LineOfBusiness>())
        foreach (var type in Enum.GetValues<AppealType>())
        foreach (var urgent in new[] { true, false })
        {
            AppealResponseDeadlinePolicy.MaxResponseWindow(lob, type, urgent)
                .Should().Be(AppealResponseDeadlinePolicy.MaxResponseWindow(lob, type, AppealLevel.FirstLevel, urgent));
        }
    }
}
