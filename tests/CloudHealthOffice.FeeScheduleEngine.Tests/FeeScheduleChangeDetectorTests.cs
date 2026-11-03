using CloudHealthOffice.FeeScheduleEngine.Domain;
using CloudHealthOffice.FeeScheduleEngine.Models;
using CloudHealthOffice.FeeScheduleEngine.Services;
using Xunit;

namespace CloudHealthOffice.FeeScheduleEngine.Tests;

/// <summary>
/// What a write to the fee schedule store changed, as retro rate reprocessing
/// reads it: which codes' rates moved and in which direction, and when a
/// schedule-level value means every line may price differently.
/// </summary>
public class FeeScheduleChangeDetectorTests
{
    private static readonly DateTime At = new(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc);

    private static FeeSchedule Schedule(long revision, params FeeScheduleLine[] lines) => new()
    {
        Id = "t1:TMHP adult:20260901",
        TenantId = "t1",
        Name = "TMHP adult",
        Type = FeeScheduleType.Medicaid,
        DefaultForPlanId = "plan-1",
        EffectiveDate = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
        Revision = revision,
        Lines = lines.ToList(),
    };

    private static FeeScheduleLine Flat(string code, decimal rate, string? modifier = null) =>
        new() { ProcedureCode = code, Modifier = modifier, RateType = FeeScheduleRateType.FlatRate, Rate = rate };

    [Fact]
    public void Create_IsRecordedWithoutCodes()
    {
        var change = FeeScheduleChangeDetector.Compare(null, Schedule(1, Flat("99213", 33.95m), Flat("99214", 50m)), At);

        Assert.Equal(FeeScheduleChangeKind.Created, change.Kind);
        Assert.Null(change.PreviousRevision);
        Assert.Equal(1, change.NewRevision);
        Assert.Equal(2, change.LinesAdded);
        Assert.Empty(change.ChangedCodes);
        Assert.False(change.AllCodes);
        Assert.Equal("t1:TMHP adult:20260901@1", change.Id);
        Assert.Equal("plan-1", change.DefaultForPlanId);
    }

    [Fact]
    public void Replace_ListsChangedCodesAndDirections()
    {
        var before = Schedule(1, Flat("99213", 33.95m), Flat("99214", 50m), Flat("99215", 70m), Flat("99211", 10m));
        var after = Schedule(2, Flat("99213", 35.10m), Flat("99214", 48m), Flat("99215", 70m), Flat("99212", 20m));

        var change = FeeScheduleChangeDetector.Compare(before, after, At);

        Assert.Equal(FeeScheduleChangeKind.Replaced, change.Kind);
        Assert.Equal(1, change.PreviousRevision);
        Assert.Equal(2, change.NewRevision);
        Assert.False(change.AllCodes);
        Assert.Equal(["99211", "99212", "99213", "99214"], change.ChangedCodes);
        Assert.Equal(1, change.LinesRateIncreased);
        Assert.Equal(1, change.LinesRateDecreased);
        Assert.Equal(1, change.LinesAdded);
        Assert.Equal(1, change.LinesRemoved);
        Assert.Equal(0, change.LinesOtherwiseChanged);
        Assert.Null(change.PreviousEffectiveDate);
    }

    [Fact]
    public void Replace_WithSameRates_ChangesNothing()
    {
        var change = FeeScheduleChangeDetector.Compare(
            Schedule(3, Flat("99213", 33.95m)), Schedule(4, Flat("99213", 33.95m)), At);

        Assert.Empty(change.ChangedCodes);
        Assert.Empty(change.ScheduleLevelChanges);
        Assert.False(change.AllCodes);
    }

    [Fact]
    public void ModifierLines_AreSeparateRates()
    {
        var change = FeeScheduleChangeDetector.Compare(
            Schedule(1, Flat("71046", 40m), Flat("71046", 10m, "26")),
            Schedule(2, Flat("71046", 40m), Flat("71046", 12m, "26")),
            At);

        Assert.Equal(["71046"], change.ChangedCodes);
        Assert.Equal(1, change.LinesRateIncreased);
    }

    [Fact]
    public void RvuChange_CountsAsOtherwiseChanged()
    {
        var before = Schedule(1, new FeeScheduleLine { ProcedureCode = "99213", RateType = FeeScheduleRateType.Rvu, WorkRvu = 1.30m });
        var after = Schedule(2, new FeeScheduleLine { ProcedureCode = "99213", RateType = FeeScheduleRateType.Rvu, WorkRvu = 1.35m });

        var change = FeeScheduleChangeDetector.Compare(before, after, At);

        Assert.Equal(["99213"], change.ChangedCodes);
        Assert.Equal(1, change.LinesOtherwiseChanged);
    }

    [Fact]
    public void ConversionFactorChange_AffectsAllCodes()
    {
        var before = Schedule(1, Flat("99213", 1m));
        before.ConversionFactor = 32.3465m;
        var after = Schedule(2, Flat("99213", 1m));
        after.ConversionFactor = 33.4009m;

        var change = FeeScheduleChangeDetector.Compare(before, after, At);

        Assert.True(change.AllCodes);
        Assert.Empty(change.ChangedCodes);
        Assert.Contains(change.ScheduleLevelChanges, c => c.StartsWith("conversion factor"));
    }

    [Fact]
    public void EffectiveDateMove_IsRecordedAndAffectsAllCodes()
    {
        var before = Schedule(1, Flat("99213", 33.95m));
        var after = Schedule(2, Flat("99213", 33.95m));
        after.EffectiveDate = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);

        var change = FeeScheduleChangeDetector.Compare(before, after, At);

        Assert.True(change.AllCodes);
        Assert.Equal(before.EffectiveDate, change.PreviousEffectiveDate);
        Assert.Equal(after.EffectiveDate, change.EffectiveDate);
    }

    [Fact]
    public void HugeChange_FallsBackToAllCodes()
    {
        var count = FeeScheduleChangeDetector.MaxListedCodes + 1;
        var before = Schedule(1, Enumerable.Range(0, count).Select(i => Flat($"C{i:D5}", 10m)).ToArray());
        var after = Schedule(2, Enumerable.Range(0, count).Select(i => Flat($"C{i:D5}", 11m)).ToArray());

        var change = FeeScheduleChangeDetector.Compare(before, after, At);

        Assert.True(change.AllCodes);
        Assert.Empty(change.ChangedCodes);
        Assert.Equal(count, change.LinesRateIncreased);
    }

    [Fact]
    public void RevenueCodeOnlyLine_IsKeyedByRevenueCode()
    {
        var change = FeeScheduleChangeDetector.Compare(
            Schedule(1, new FeeScheduleLine { RevenueCode = "0120", RateType = FeeScheduleRateType.FlatRate, Rate = 900m }),
            Schedule(2, new FeeScheduleLine { RevenueCode = "0120", RateType = FeeScheduleRateType.FlatRate, Rate = 950m }),
            At);

        Assert.Equal(["rev:0120"], change.ChangedCodes);
    }
}
