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
