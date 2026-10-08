using CoverageService.Models;

namespace CoverageService.Tests.Models;

/// <summary>
/// Date-of-service eligibility is decided by the effective/termination span,
/// not by the coverage's current status. A terminated coverage still covers
/// service dates on or before its termination date.
/// </summary>
public class CoverageIsActiveOnTests
{
    private static Coverage Build(CoverageStatus status, DateTime effective, DateTime? termination = null) => new()
    {
        TenantId = "t1",
        MemberId = "M1",
        GroupNumber = "G",
        PlanId = "P",
        Status = status,
        EffectiveDate = effective,
        TerminationDate = termination
    };

    private static readonly DateTime Effective = new(2025, 1, 1);
    private static readonly DateTime Termination = new(2025, 6, 30);

    [Theory]
    [InlineData("2025-01-01")]
    [InlineData("2025-03-15")]
    [InlineData("2025-06-30")]
    public void Terminated_ServiceDateWithinSpan_IsActive(string dos)
    {
        Build(CoverageStatus.Terminated, Effective, Termination)
            .IsActiveOn(DateTime.Parse(dos)).Should().BeTrue();
    }

    [Theory]
    [InlineData("2025-07-01")]
    [InlineData("2024-12-31")]
    public void Terminated_ServiceDateOutsideSpan_IsNotActive(string dos)
    {
        Build(CoverageStatus.Terminated, Effective, Termination)
            .IsActiveOn(DateTime.Parse(dos)).Should().BeFalse();
    }

    [Fact]
    public void RetroTerminated_ServiceDateAfterRetroTermDate_IsNotActive()
    {
        // Retro-term moved the end date back to 2025-03-31.
        var coverage = Build(CoverageStatus.Terminated, Effective, new DateTime(2025, 3, 31));
        coverage.IsActiveOn(new DateTime(2025, 3, 31)).Should().BeTrue();
        coverage.IsActiveOn(new DateTime(2025, 4, 1)).Should().BeFalse();
    }

    [Fact]
    public void ActiveOpenEnded_IsActiveFromEffectiveDateOnward()
    {
        var coverage = Build(CoverageStatus.Active, Effective);
        coverage.IsActiveOn(new DateTime(2030, 1, 1)).Should().BeTrue();
        coverage.IsActiveOn(new DateTime(2024, 12, 31)).Should().BeFalse();
    }

    [Theory]
    [InlineData(CoverageStatus.Active)]
    [InlineData(CoverageStatus.Terminated)]
    [InlineData(CoverageStatus.COBRA)]
    public void InForceStatuses_WithinSpan_AreActive(CoverageStatus status)
    {
        Build(status, Effective, Termination).IsActiveOn(new DateTime(2025, 3, 1)).Should().BeTrue();
    }

    [Theory]
    [InlineData(CoverageStatus.Pending)]
    [InlineData(CoverageStatus.Suspended)]
    public void NotInForceStatuses_WithinSpan_AreNotActive(CoverageStatus status)
    {
        // Pending may not be effectuated yet (e.g. binder payment outstanding).
        Build(status, Effective, Termination)
            .IsActiveOn(new DateTime(2025, 3, 1)).Should().BeFalse();
    }

    [Fact]
    public void UnknownStatus_WithinSpan_IsNotActive()
    {
        // Allow-list: a status added later (e.g. a void/cancel) must opt in.
        Build((CoverageStatus)99, Effective, Termination)
            .IsActiveOn(new DateTime(2025, 3, 1)).Should().BeFalse();
    }
}
