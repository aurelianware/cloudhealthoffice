using CoverageService.Models;

namespace CoverageService.Tests.Models;

/// <summary>
/// The termination date drives the current status: Terminated once the date
/// is today or past, never before. Reinstatement reverses a termination.
/// </summary>
public class CoverageTerminationTests
{
    private static readonly DateTime Today = new(2025, 7, 1);

    private static Coverage Build(CoverageStatus status, DateTime? termination = null, bool cobra = false) => new()
    {
        TenantId = "t1",
        MemberId = "M1",
        GroupNumber = "G",
        PlanId = "P",
        Status = status,
        EffectiveDate = new DateTime(2025, 1, 1),
        TerminationDate = termination,
        IsCOBRA = cobra
    };

    [Theory]
    [InlineData("2025-06-30")]
    [InlineData("2025-07-01")]
    public void ApplyTermination_TodayOrPast_TerminatesNow(string date)
    {
        var coverage = Build(CoverageStatus.Active);
        coverage.ApplyTermination(DateTime.Parse(date), Today);

        coverage.Status.Should().Be(CoverageStatus.Terminated);
        coverage.TerminationDate.Should().Be(DateTime.Parse(date));
    }

    [Theory]
    [InlineData(CoverageStatus.Active)]
    [InlineData(CoverageStatus.COBRA)]
    [InlineData(CoverageStatus.Pending)]
    public void ApplyTermination_FutureDate_KeepsCurrentStatus(CoverageStatus status)
    {
        var coverage = Build(status);
        coverage.ApplyTermination(new DateTime(2025, 7, 31), Today);

        coverage.Status.Should().Be(status);
        coverage.TerminationDate.Should().Be(new DateTime(2025, 7, 31));
        // Still in force through the termination date, not after.
        coverage.IsActiveOn(new DateTime(2025, 7, 31)).Should().Be(status != CoverageStatus.Pending);
        coverage.IsActiveOn(new DateTime(2025, 8, 1)).Should().BeFalse();
    }

    [Fact]
    public void ApplyTermination_MovingTerminatedCoverageEndIntoTheFuture_PutsItBackInForce()
    {
        var coverage = Build(CoverageStatus.Terminated, new DateTime(2025, 6, 30));
        coverage.ApplyTermination(new DateTime(2025, 12, 31), Today);

        coverage.Status.Should().Be(CoverageStatus.Active);
        coverage.TerminationDate.Should().Be(new DateTime(2025, 12, 31));
    }

    [Fact]
    public void ApplyTermination_StripsTimeOfDay()
    {
        var coverage = Build(CoverageStatus.Active);
        coverage.ApplyTermination(new DateTime(2025, 7, 1, 18, 30, 0), Today);

        coverage.TerminationDate.Should().Be(new DateTime(2025, 7, 1));
        coverage.Status.Should().Be(CoverageStatus.Terminated);
    }

    [Theory]
    [InlineData(CoverageStatus.Terminated, false, CoverageStatus.Active)]
    [InlineData(CoverageStatus.Suspended, false, CoverageStatus.Active)]
    [InlineData(CoverageStatus.Terminated, true, CoverageStatus.COBRA)]
    [InlineData(CoverageStatus.Active, false, CoverageStatus.Active)]
    [InlineData(CoverageStatus.Pending, false, CoverageStatus.Pending)]
    public void Reinstate_ClearsTerminationAndRestoresInForceStatus(
        CoverageStatus status, bool cobra, CoverageStatus expected)
    {
        var coverage = Build(status, new DateTime(2025, 6, 30), cobra);
        coverage.Reinstate();

        coverage.TerminationDate.Should().BeNull();
        coverage.Status.Should().Be(expected);
        coverage.EffectiveDate.Should().Be(new DateTime(2025, 1, 1), "the span stays continuous");
    }

    [Theory]
    [InlineData(CoverageStatus.Active, "2025-07-01", CoverageStatus.Terminated)]
    [InlineData(CoverageStatus.COBRA, "2025-06-30", CoverageStatus.Terminated)]
    [InlineData(CoverageStatus.Suspended, "2025-06-30", CoverageStatus.Terminated)]
    public void DueStatusTransition_TerminationDateReached_IsTerminated(
        CoverageStatus status, string termination, CoverageStatus expected)
    {
        Build(status, DateTime.Parse(termination)).DueStatusTransition(Today).Should().Be(expected);
    }

    [Theory]
    [InlineData("2025-07-01", false, CoverageStatus.Active)]  // effective today
    [InlineData("2025-06-01", false, CoverageStatus.Active)]  // effective in the past
    [InlineData("2025-07-01", true, CoverageStatus.COBRA)]
    public void DueStatusTransition_PendingWhoseEffectiveDateArrived_IsInForce(
        string effective, bool cobra, CoverageStatus expected)
    {
        var coverage = Build(CoverageStatus.Pending, cobra: cobra);
        coverage.EffectiveDate = DateTime.Parse(effective);

        coverage.DueStatusTransition(Today).Should().Be(expected);
        coverage.CurrentStatus(Today).Should().Be(expected);
    }

    [Fact]
    public void DueStatusTransition_PendingNotYetEffective_StaysPending()
    {
        var coverage = Build(CoverageStatus.Pending);
        coverage.EffectiveDate = new DateTime(2025, 7, 2);

        coverage.DueStatusTransition(Today).Should().BeNull();
        coverage.CurrentStatus(Today).Should().Be(CoverageStatus.Pending);
    }

    [Fact]
    public void DueStatusTransition_PendingWhoseTerminationAlsoPassed_IsTerminated()
    {
        var coverage = Build(CoverageStatus.Pending, new DateTime(2025, 6, 30));
        coverage.DueStatusTransition(Today).Should().Be(CoverageStatus.Terminated);
    }

    [Fact]
    public void DueStatusTransition_NothingDue_IsNull()
    {
        Build(CoverageStatus.Active, new DateTime(2025, 7, 2)).DueStatusTransition(Today).Should().BeNull();
        Build(CoverageStatus.Active).DueStatusTransition(Today).Should().BeNull();
        Build(CoverageStatus.Terminated, new DateTime(2025, 6, 30)).DueStatusTransition(Today).Should().BeNull();
    }
}
