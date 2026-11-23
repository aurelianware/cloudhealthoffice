namespace CloudHealthOffice.Tools.MccPlatformValidator;

/// <summary>
/// How long the validator waits between reads of a submitted claim's status.
///
/// The validator holds one claim per worker until the claim is terminal, so a
/// worker's claims per second is one over the time it waits. With a fixed
/// 1,000 ms poll, a claim that finished in 200 ms still held its worker for a
/// full second: measured throughput could not exceed parallelism per second
/// (96 workers, about 96 to 160 claims/sec), whatever the platform could do.
///
/// The wait now starts short and doubles up to the configured interval, so a
/// fast claim is seen soon after it finishes and a slow one is not polled
/// harder than before: 50, 100, 200, 400, 800, then 1,000 ms.
/// </summary>
public static class MccObservationBackoff
{
    public const int DefaultInitialMilliseconds = 50;

    public static TimeSpan Initial(TimeSpan initial, TimeSpan max) =>
        initial <= TimeSpan.Zero || initial > max ? max : initial;

    public static TimeSpan Next(TimeSpan current, TimeSpan max)
    {
        var doubled = TimeSpan.FromTicks(Math.Min(current.Ticks * 2, max.Ticks));
        return doubled <= TimeSpan.Zero ? max : doubled;
    }
}
