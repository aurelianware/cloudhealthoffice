using CoverageService.Models;
using CoverageService.Services;

namespace CoverageService.Tests.Services;

public class PcpCoverageIntervalsTests
{
    private static readonly DateTime Day = new(2026, 10, 25);
    private static Coverage Row(string member, int start, int? end = null) => new()
    {
        MemberId = member, EffectiveDate = Day.AddDays(start), TerminationDate = end.HasValue ? Day.AddDays(end.Value) : null,
        InsuranceLineCode = InsuranceLineCodes.Health, Status = CoverageStatus.Active,
    };

    [Fact]
    public void DetectsLaterPeak_AndCountsMembersInsteadOfCoverageRows()
    {
        var before = new[] { Row("future", 5), Row("future", 6) };
        var after = before.Append(Row("new", 0)).ToArray();
        var days = PcpCoverageIntervals.AddedMemberBoundaries(before, after, "new");
        days.Should().Contain(Day.AddDays(5));
        PcpCoverageIntervals.Count(after, Day).Should().Be(1);
        PcpCoverageIntervals.Count(after, Day.AddDays(6)).Should().Be(2);
    }

    [Fact]
    public void TerminationIsInclusive_AndAssignmentDateClampsOccupancy()
    {
        var row = Row("one", -30, 0); row.PcpAssignmentDate = Day.AddDays(-1);
        PcpCoverageIntervals.Count(new[] { row }, Day.AddDays(-2)).Should().Be(0);
        PcpCoverageIntervals.Count(new[] { row }, Day).Should().Be(1);
        PcpCoverageIntervals.Count(new[] { row }, Day.AddDays(1)).Should().Be(0);
    }

    [Fact]
    public void MedicalAndLegacyOnly_WithPendingReservations_AndMalformedRowsClosed()
    {
        var dental = Row("dental", 0); dental.InsuranceLineCode = InsuranceLineCodes.Dental;
        var suspended = Row("suspended", 0); suspended.Status = CoverageStatus.Suspended;
        var invalid = Row("invalid", 0); invalid.Status = CoverageStatus.Terminated;
        var future = Row("future", 2); future.Status = CoverageStatus.Pending;
        var legacy = Row("legacy", 0); legacy.InsuranceLineCode = null;
        var rows = new[] { dental, suspended, invalid, future, legacy };
        PcpCoverageIntervals.Count(rows, Day).Should().Be(1);
        PcpCoverageIntervals.Count(rows, Day.AddDays(2)).Should().Be(2);
    }

    [Fact]
    public void RemovingOccupancy_AndAddingDuplicateCoverage_NeedNoNewSlot()
    {
        var before = new[] { Row("one", 0) };
        PcpCoverageIntervals.AddedMemberBoundaries(before, new[] { Row("one", 0, 2) }, "one").Should().BeEmpty();
        PcpCoverageIntervals.AddedMemberBoundaries(before, before.Append(Row("one", 1)).ToArray(), "one").Should().BeEmpty();
    }

    [Fact]
    public void ReactivationAndExtensionExposeTheFirstNewMemberDay()
    {
        var suspended = Row("one", 0); suspended.Status = CoverageStatus.Suspended;
        PcpCoverageIntervals.AddedMemberBoundaries(new[] { suspended }, new[] { Row("one", 0) }, "one").Should().Contain(Day);
        PcpCoverageIntervals.AddedMemberBoundaries(new[] { Row("one", 0, 1) }, new[] { Row("one", 0, 3) }, "one")
            .Should().Contain(Day.AddDays(2));
    }
}
