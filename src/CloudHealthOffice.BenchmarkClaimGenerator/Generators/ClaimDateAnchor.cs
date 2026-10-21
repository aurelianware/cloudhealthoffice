namespace CloudHealthOffice.BenchmarkClaimGenerator.Generators;

/// <summary>
/// Anchors generated claim dates to a reference ("as of") date so a corpus
/// never contains a service, admission, discharge or receipt date after the
/// day it describes. Generation stays seed-deterministic: the same seed and
/// the same reference date always produce the same corpus; only the default
/// reference date (today, UTC) moves from run to run.
/// </summary>
public static class ClaimDateAnchor
{
    /// <summary>
    /// Width of the window service dates are drawn from. With the longest
    /// tail (an 89-day stay plus receipt lag) the oldest service date is
    /// under 300 days before the reference date, so every claim is inside a
    /// 365-day timely filing limit even when the corpus is submitted weeks
    /// after it was generated (claims-service TimelyFilingStage denies late
    /// claims with CARC 29).
    /// </summary>
    public const int ServiceWindowDays = 180;

    /// <summary>The most days any generated service date is before the reference date.</summary>
    public const int MaxServiceAgeDays = 300;

    /// <summary>
    /// The reference date for a run: the supplied date, or today (UTC),
    /// truncated to a calendar day. Returned with
    /// <see cref="DateTimeKind.Unspecified"/> like every other date the
    /// generators emit.
    /// </summary>
    public static DateTime Resolve(DateTime? referenceDate) =>
        DateTime.SpecifyKind((referenceDate ?? DateTime.UtcNow).Date, DateTimeKind.Unspecified);

    /// <summary>
    /// Draw a service (or admission) date from the
    /// <see cref="ServiceWindowDays"/>-day window that ends
    /// <paramref name="maxTailDays"/> + 1 days before
    /// <paramref name="referenceDate"/>, so any date up to
    /// <paramref name="maxTailDays"/> after it (length of stay, receipt lag)
    /// still falls before the reference date. Consumes exactly one
    /// <c>random.Next(ServiceWindowDays)</c>, the same draw the generators
    /// made against their former fixed 2024 base date (over a
    /// 365-day window before October 2026).
    /// </summary>
    public static DateTime DrawServiceDate(DateTime referenceDate, int maxTailDays, Random random)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxTailDays);
        var windowStart = referenceDate.Date.AddDays(-(ServiceWindowDays + maxTailDays));
        return windowStart.AddDays(random.Next(ServiceWindowDays));
    }
}
