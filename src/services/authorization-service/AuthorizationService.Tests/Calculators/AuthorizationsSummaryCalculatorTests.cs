using AuthorizationService.Models;

namespace AuthorizationService.Tests.Calculators;

public class AuthorizationsSummaryCalculatorTests
{
    [Fact]
    public void GetSummary_UsesResumedDate_WhenSlaResumedAtIsSet()
    {
        // Arrange
        var auth = new Authorization
        {
            SubmittedDate = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
            SlaResumedAt = new DateTime(2026, 3, 10, 0, 0, 0, DateTimeKind.Utc),
            ReviewedDate = new DateTime(2026, 3, 12, 0, 0, 0, DateTimeKind.Utc),
        };

        // Act
        var turnaround = AuthorizationsSummaryCalculator.CalculateTurnaroundDays(auth);

        // Assert — should be 2 days (Mar 10 → Mar 12), NOT 11 days (Mar 1 → Mar 12)
        turnaround.Should().Be(2);
    }

    [Fact]
    public void GetSummary_UsesSubmittedDate_WhenSlaResumedAtIsNull()
    {
        // Arrange
        var auth = new Authorization
        {
            SubmittedDate = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
            SlaResumedAt = null,
            ReviewedDate = new DateTime(2026, 3, 4, 0, 0, 0, DateTimeKind.Utc),
        };

        // Act
        var turnaround = AuthorizationsSummaryCalculator.CalculateTurnaroundDays(auth);

        // Assert — should be 3 days (Mar 1 → Mar 4)
        turnaround.Should().Be(3);
    }

    [Fact]
    public void CalculateTurnaroundDays_WhenSlaResumedAtBeforeSubmitted_UsesSubmittedDate()
    {
        var auth = new Authorization
        {
            SubmittedDate = new DateTime(2026, 3, 10, 0, 0, 0, DateTimeKind.Utc),
            SlaResumedAt = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc), // Before submitted
            ReviewedDate = new DateTime(2026, 3, 12, 0, 0, 0, DateTimeKind.Utc),
        };

        var turnaround = AuthorizationsSummaryCalculator.CalculateTurnaroundDays(auth);

        // Should use SubmittedDate (Mar 10) since it's later: 2 days
        turnaround.Should().Be(2);
    }
}
