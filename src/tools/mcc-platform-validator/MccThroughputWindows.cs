using System.Globalization;
using System.Text;

namespace CloudHealthOffice.Tools.MccPlatformValidator;

/// <summary>
/// Claims per second and latency for each fixed slice of a run's wall clock.
///
/// A run's headline throughput is one average over the whole run. A platform
/// that starts at 300 claims/sec and ends at 80 can average the same as one
/// that holds 150 throughout, and only the second can carry a steady daily
/// volume. Recording each window (one minute by default) shows whether the
/// rate holds as stored data grows, which is the question a multi-hour run
/// exists to answer.
/// </summary>
public sealed class MccThroughputWindows
{
    private readonly object _gate = new();
    private readonly TimeSpan _width;
    private readonly List<double> _latencies = new();
    private readonly List<MccThroughputWindow> _closed = new();
    private TimeSpan _windowStart = TimeSpan.Zero;
    private long _completedBefore;
    private int _incomplete;

    public MccThroughputWindows(TimeSpan width)
    {
        _width = width > TimeSpan.Zero ? width : TimeSpan.FromMinutes(1);
    }

    public IReadOnlyList<MccThroughputWindow> Windows
    {
        get { lock (_gate) return _closed.ToList(); }
    }

    /// <summary>
    /// Records one finished claim at <paramref name="elapsed"/> since timing
    /// started. Returns the windows this call closed (usually none), so the
    /// caller can print them.
    /// </summary>
    public IReadOnlyList<MccThroughputWindow> Record(TimeSpan elapsed, double latencyMilliseconds, bool incomplete)
    {
        lock (_gate)
        {
            var closed = CloseThrough(elapsed);
            _latencies.Add(latencyMilliseconds);
            if (incomplete) _incomplete++;
            return closed;
        }
    }

    /// <summary>Closes the last, partial window at the end of the run.</summary>
    public MccThroughputWindow? Complete(TimeSpan elapsed)
    {
        lock (_gate)
        {
            var closed = CloseThrough(elapsed);
            if (_latencies.Count > 0 && elapsed > _windowStart) return Close(elapsed);
            return closed.Count > 0 ? closed[^1] : null;
        }
    }

    private List<MccThroughputWindow> CloseThrough(TimeSpan elapsed)
    {
        var closed = new List<MccThroughputWindow>();
        while (elapsed >= _windowStart + _width)
        {
            closed.Add(Close(_windowStart + _width));
        }
        return closed;
    }

    private MccThroughputWindow Close(TimeSpan end)
    {
        var sorted = _latencies.ToArray();
        Array.Sort(sorted);
        var seconds = Math.Max(0.001, (end - _windowStart).TotalSeconds);
        var window = new MccThroughputWindow(
            _closed.Count + 1,
            _windowStart,
            end,
            _completedBefore,
            _completedBefore + sorted.Length,
            sorted.Length / seconds,
            Percentile(sorted, 0.50),
            Percentile(sorted, 0.95),
            Percentile(sorted, 0.99),
            _incomplete);
        _closed.Add(window);
        _completedBefore += sorted.Length;
        _latencies.Clear();
        _incomplete = 0;
        _windowStart = end;
        return window;
    }

    private static double Percentile(double[] sorted, double p)
    {
        if (sorted.Length == 0) return 0;
        var index = (int)Math.Ceiling(p * sorted.Length) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
    }

    public static string FormatLine(MccThroughputWindow w) =>
        string.Create(CultureInfo.InvariantCulture,
            $"  [window {w.Index,4}] {w.Start:hh\\:mm\\:ss}-{w.End:hh\\:mm\\:ss}  claims {w.CompletedAtEnd,10:N0}  " +
            $"{w.ClaimsPerSecond,8:N2} claims/sec  p50 {w.P50Milliseconds,6:N0} ms  p95 {w.P95Milliseconds,6:N0} ms  " +
            $"p99 {w.P99Milliseconds,6:N0} ms  incomplete {w.Incomplete:N0}");

    public static string ToCsv(IEnumerable<MccThroughputWindow> windows)
    {
        var sb = new StringBuilder("window,start_seconds,end_seconds,completed_before,completed_at_end,claims_per_second,p50_ms,p95_ms,p99_ms,incomplete\n");
        foreach (var w in windows)
        {
            sb.Append(string.Create(CultureInfo.InvariantCulture,
                $"{w.Index},{w.Start.TotalSeconds:F1},{w.End.TotalSeconds:F1},{w.CompletedBefore},{w.CompletedAtEnd},{w.ClaimsPerSecond:F2},{w.P50Milliseconds:F0},{w.P95Milliseconds:F0},{w.P99Milliseconds:F0},{w.Incomplete}\n"));
        }
        return sb.ToString();
    }
}

public sealed record MccThroughputWindow(
    int Index,
    TimeSpan Start,
    TimeSpan End,
    long CompletedBefore,
    long CompletedAtEnd,
    double ClaimsPerSecond,
    double P50Milliseconds,
    double P95Milliseconds,
    double P99Milliseconds,
    int Incomplete);
