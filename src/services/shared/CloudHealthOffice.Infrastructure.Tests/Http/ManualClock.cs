namespace CloudHealthOffice.Infrastructure.Tests.Http;

/// <summary>
/// A clock tests move by hand, timers included: a resilience pipeline's
/// timeouts and retry delays, and <c>Task.Delay(d, clock)</c>, fire when
/// <see cref="Advance"/> moves past their due time, so a test of a 25 s call
/// takes no real 25 s. A fake downstream handler "takes" time by advancing
/// the clock itself; <see cref="Drive{T}"/> moves the clock in small steps
/// meanwhile, for the retry delays between attempts.
/// <para>
/// <see cref="HttpClient.Timeout"/> runs on real time and is not moved by it.
/// </para>
/// </summary>
public sealed class ManualClock : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = new();
    private readonly DateTimeOffset _start;
    private DateTimeOffset _now;

    public ManualClock(DateTimeOffset? start = null) => _now = _start = start ?? DateTimeOffset.UtcNow;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate) return _now;
    }

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp()
    {
        lock (_gate) return _now.UtcTicks;
    }

    /// <summary>How far the clock has moved since it was made.</summary>
    public TimeSpan Elapsed
    {
        get { lock (_gate) return _now - _start; }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>Moves the clock forward, firing every timer that falls due on the way, in order.</summary>
    public void Advance(TimeSpan by)
    {
        DateTimeOffset target;
        lock (_gate) target = _now + by;
        while (true)
        {
            ManualTimer? next;
            lock (_gate)
            {
                next = _timers.Where(t => t.Due <= target).MinBy(t => t.Due);
                if (next is null)
                {
                    _now = target;
                    return;
                }
                if (next.Due > _now) _now = next.Due;
                if (next.Period > TimeSpan.Zero && next.Period != Timeout.InfiniteTimeSpan)
                    next.Due += next.Period;
                else
                    _timers.Remove(next);
            }
            next.Fire();
        }
    }

    /// <summary>
    /// Waits for <paramref name="work"/>, moving the clock <paramref name="step"/> at a
    /// time (default 50 ms) while it runs, so retry delays elapse. Fails if the work is
    /// still running after <paramref name="limit"/> of clock time (default an hour).
    /// </summary>
    public async Task<T> Drive<T>(Task<T> work, TimeSpan? step = null, TimeSpan? limit = null)
    {
        var by = step ?? TimeSpan.FromMilliseconds(50);
        var until = Elapsed + (limit ?? TimeSpan.FromHours(1));
        while (!work.IsCompleted)
        {
            if (Elapsed > until)
                throw new TimeoutException($"The work did not finish within {limit ?? TimeSpan.FromHours(1)} of clock time.");
            await Task.WhenAny(work, Task.Delay(1));
            if (!work.IsCompleted) Advance(by);
        }
        return await work;
    }

    /// <inheritdoc cref="Drive{T}(Task{T}, TimeSpan?, TimeSpan?)"/>
    public Task Drive(Task work, TimeSpan? step = null, TimeSpan? limit = null) =>
        Drive(Wrap(work), step, limit);

    private static async Task<bool> Wrap(Task work)
    {
        await work;
        return true;
    }

    private sealed class ManualTimer : ITimer
    {
        private readonly ManualClock _clock;
        private readonly TimerCallback _callback;
        private readonly object? _state;

        public ManualTimer(ManualClock clock, TimerCallback callback, object? state)
        {
            _clock = clock;
            _callback = callback;
            _state = state;
        }

        public DateTimeOffset Due { get; set; }
        public TimeSpan Period { get; private set; }

        public void Fire() => _callback(_state);

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (_clock._gate)
            {
                _clock._timers.Remove(this);
                if (dueTime == Timeout.InfiniteTimeSpan) return true;
                Due = _clock._now + dueTime;
                Period = period;
                _clock._timers.Add(this);
            }
            return true;
        }

        public void Dispose()
        {
            lock (_clock._gate) _clock._timers.Remove(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return default;
        }
    }
}
