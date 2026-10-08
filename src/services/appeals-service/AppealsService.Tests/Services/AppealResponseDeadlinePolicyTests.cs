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

    [Theory]
    [InlineData(LineOfBusiness.Medicare, AppealType.Reconsideration, true, 72)]
    [InlineData(LineOfBusiness.Medicaid, AppealType.Reconsideration, true, 72)]
    [InlineData(LineOfBusiness.Commercial, AppealType.PeerReview, true, 72)]
    [InlineData(LineOfBusiness.Medicare, AppealType.Reconsideration, false, 30 * 24)]
    [InlineData(LineOfBusiness.Medicaid, AppealType.Reconsideration, false, 30 * 24)]
    [InlineData(LineOfBusiness.Commercial, AppealType.Reconsideration, false, 30 * 24)]
    [InlineData(LineOfBusiness.Medicare, AppealType.Grievance, false, 30 * 24)]  // 422.564(e)
    [InlineData(LineOfBusiness.Medicare, AppealType.Grievance, true, 30 * 24)]   // 24h is default only
    [InlineData(LineOfBusiness.Medicaid, AppealType.Grievance, false, 90 * 24)]  // 438.408(b)(1)
    [InlineData(LineOfBusiness.Medicaid, AppealType.Grievance, true, 90 * 24)]   // 72h is default only
    public void EnforceableMaximum_Is_Regulatory_Ceiling(LineOfBusiness lob, AppealType type, bool urgent, int hours)
    {
        AppealResponseDeadlinePolicy.ComputeEnforceableMaximum(Received, lob, type, urgent)
            .Should().Be(Received.AddHours(hours));
    }

    [Theory]
    [InlineData(LineOfBusiness.Commercial, false)]
    [InlineData(LineOfBusiness.Commercial, true)]
    [InlineData(LineOfBusiness.Marketplace, false)]
    [InlineData(LineOfBusiness.Marketplace, true)]
    public void Commercial_Grievance_Has_No_Federal_Maximum(LineOfBusiness lob, bool urgent)
    {
        AppealResponseDeadlinePolicy.ComputeEnforceableMaximum(Received, lob, AppealType.Grievance, urgent)
            .Should().BeNull();
    }

    [Fact]
    public void Default_Target_Never_Exceeds_Enforceable_Maximum()
    {
        foreach (var lob in Enum.GetValues<LineOfBusiness>())
        foreach (var type in Enum.GetValues<AppealType>())
        foreach (var urgent in new[] { true, false })
        {
            var max = AppealResponseDeadlinePolicy.EnforceableMaximumWindow(lob, type, urgent);
            if (max is null) continue;
            AppealResponseDeadlinePolicy.MaxResponseWindow(lob, type, urgent)
                .Should().BeLessThanOrEqualTo(max.Value, $"{lob} {type} urgent={urgent}");
        }
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
}
