using CloudHealthOffice.Tools.MccPlatformValidator;

namespace CloudHealthOffice.MccPlatformValidator.Tests;

public class MccObservationBackoffTests
{
    private static readonly TimeSpan Max = TimeSpan.FromMilliseconds(1000);

    [Fact]
    public void TheWait_StartsShort_AndDoublesUpToTheInterval()
    {
        var wait = MccObservationBackoff.Initial(TimeSpan.FromMilliseconds(50), Max);
        var waits = new List<double> { wait.TotalMilliseconds };
        for (var i = 0; i < 6; i++)
        {
            wait = MccObservationBackoff.Next(wait, Max);
            waits.Add(wait.TotalMilliseconds);
        }

        Assert.Equal([50, 100, 200, 400, 800, 1000, 1000], waits);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(5000)]
    public void AnInitialWaitOutsideTheRange_UsesTheInterval(int initialMs)
    {
        Assert.Equal(Max, MccObservationBackoff.Initial(TimeSpan.FromMilliseconds(initialMs), Max));
    }
}

public class MccThroughputWindowsTests
{
    [Fact]
    public void EachWindow_CountsTheClaimsThatFinishedInIt()
    {
        var windows = new MccThroughputWindows(TimeSpan.FromSeconds(10));

        for (var i = 0; i < 100; i++) Assert.Empty(windows.Record(TimeSpan.FromSeconds(i * 0.09), 200, false));
        var closed = windows.Record(TimeSpan.FromSeconds(10.5), 400, false);
        Assert.Single(closed);
        for (var i = 0; i < 49; i++) windows.Record(TimeSpan.FromSeconds(11 + i * 0.1), 400, i == 0);
        var last = windows.Complete(TimeSpan.FromSeconds(20));

        var first = closed[0];
        Assert.Equal(1, first.Index);
        Assert.Equal(0, first.CompletedBefore);
        Assert.Equal(100, first.CompletedAtEnd);
        Assert.Equal(10.0, first.ClaimsPerSecond, 3);
        Assert.Equal(200, first.P95Milliseconds);

        Assert.NotNull(last);
        Assert.Equal(100, last!.CompletedBefore);
        Assert.Equal(150, last.CompletedAtEnd);
        Assert.Equal(5.0, last.ClaimsPerSecond, 3);
        Assert.Equal(1, last.Incomplete);
        Assert.Equal(2, windows.Windows.Count);
    }

    [Fact]
    public void AQuietGap_ClosesEmptyWindows_SoAStallShowsAsZero()
    {
        var windows = new MccThroughputWindows(TimeSpan.FromSeconds(10));
        windows.Record(TimeSpan.FromSeconds(1), 100, false);

        var closed = windows.Record(TimeSpan.FromSeconds(35), 100, false);

        Assert.Equal(3, closed.Count);
        Assert.Equal([0.1, 0.0, 0.0], closed.Select(w => Math.Round(w.ClaimsPerSecond, 3)).ToArray());
    }

    [Fact]
    public void TheCsv_HasOneRowPerWindow()
    {
        var windows = new MccThroughputWindows(TimeSpan.FromSeconds(10));
        windows.Record(TimeSpan.FromSeconds(1), 100, false);
        windows.Record(TimeSpan.FromSeconds(12), 300, false);
        windows.Complete(TimeSpan.FromSeconds(15));

        var lines = MccThroughputWindows.ToCsv(windows.Windows).TrimEnd('\n').Split('\n');

        Assert.Equal(3, lines.Length);
        Assert.StartsWith("window,start_seconds", lines[0]);
        Assert.StartsWith("1,0.0,10.0,0,1,0.10,", lines[1]);
        Assert.StartsWith("2,10.0,15.0,1,2,0.20,", lines[2]);
    }

    [Fact]
    public void Parse_ReadsTheMeasurementOptions()
    {
        var options = ValidatorOptions.Parse([
            "--pend-observation-initial-interval-ms", "25",
            "--throughput-window-seconds", "30",
            "--throughput-windows-csv", "/tmp/w.csv",
        ]);

        Assert.Equal(25, options.PendObservationInitialIntervalMilliseconds);
        Assert.Equal(30, options.ThroughputWindowSeconds);
        Assert.Equal("/tmp/w.csv", options.ThroughputWindowsCsvPath);
    }

    [Fact]
    public void Parse_DefaultsToAShortFirstPoll_AndOneMinuteWindows()
    {
        var options = ValidatorOptions.Parse([]);

        Assert.Equal(50, options.PendObservationInitialIntervalMilliseconds);
        Assert.Equal(1000, options.PendObservationIntervalMilliseconds);
        Assert.Equal(60, options.ThroughputWindowSeconds);
        Assert.Null(options.ThroughputWindowsCsvPath);
    }
}
