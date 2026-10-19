using AuthorizationService.Models;
using AuthorizationService.Services;

namespace AuthorizationService.Tests.Services;

public class AuthorizationDeadlinePolicyTests
{
    [Theory]
    [InlineData("U", 72)]
    [InlineData("u", 72)]
    [InlineData("E", 168)]
    [InlineData(null, 168)]
    public void DeadlineRunsFromReceiptIncludingWeekends(string? urgency, int hours)
    {
        var receipt = new DateTime(2026, 10, 16, 15, 30, 0, DateTimeKind.Utc);
        var auth = new Authorization { SubmittedDate = receipt, LevelOfService = urgency };
        AuthorizationDeadlinePolicy.Initialize(auth);
        auth.DecisionReceivedAt.Should().Be(receipt);
        auth.DecisionDueAt.Should().Be(receipt.AddHours(hours));
        auth.DecisionDeadlinePolicy.Should().Be(AuthorizationDeadlinePolicy.Version);
    }

    [Fact]
    public void DocumentArrivalCannotMoveDeadlineOrHideBreach()
    {
        var receipt = new DateTime(2026, 10, 1, 10, 0, 0, DateTimeKind.Utc);
        var auth = new Authorization { SubmittedDate = receipt, LevelOfService = "U", Status = AuthorizationStatus.Pended };
        AuthorizationDeadlinePolicy.Initialize(auth);
        auth.SlaResumedAt = receipt.AddDays(5);
        var status = SlaWatchdogService.ComputeSlaStatus(auth, receipt.AddDays(5));
        status.SlaStartedAt.Should().Be(receipt);
        status.SlaDeadline.Should().Be(receipt.AddHours(72));
        status.EscalationLevel.Should().Be(SlaEscalationLevel.Breach);
        status.HoursRemaining.Should().Be(-48);
    }
}
