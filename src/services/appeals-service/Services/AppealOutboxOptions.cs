namespace AppealsService.Services;

/// <summary>
/// <c>AppealOutbox</c> configuration section for the transactional outbox
/// relay (<c>HostedServices.AppealOutboxDispatcher</c>). TimeSpans use the
/// <c>hh:mm:ss</c> form.
/// </summary>
public sealed class AppealOutboxOptions
{
    public const string SectionName = "AppealOutbox";

    /// <summary>Run the background relay loop. Off only for tests; events then wait for an inline dispatch.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Publish an appeal's outbox right after the request that changed it
    /// (low latency). The background loop remains the delivery guarantee.
    /// </summary>
    public bool DispatchInline { get; set; } = true;

    /// <summary>Sleep between sweeps for pending entries.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Appeals visited per sweep.</summary>
    public int BatchSize { get; set; } = 50;

    /// <summary>
    /// Non-transient publish failures before an entry is dead-lettered.
    /// Transient failures (Kafka unreachable) never count toward this.
    /// </summary>
    public int MaxAttempts { get; set; } = 10;

    /// <summary>First retry delay; doubles per failure up to <see cref="MaxBackoff"/>.</summary>
    public TimeSpan InitialBackoff { get; set; } = TimeSpan.FromSeconds(1);

    public TimeSpan MaxBackoff { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Per-appeal dispatch lease; another replica takes over after it lapses.</summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How long sent / skipped entries stay on the appeal before they are pruned.</summary>
    public TimeSpan SentRetention { get; set; } = TimeSpan.FromDays(1);

    /// <summary>
    /// Kafka disabled by configuration (<c>Kafka:BootstrapServers</c> unset):
    /// false (default) keeps events pending, so enabling Kafka later
    /// delivers the backlog; true marks them <c>Skipped</c> instead.
    /// </summary>
    public bool SkipWhenKafkaDisabled { get; set; }

    /// <summary>Exponential backoff for the given number of failures (1-based).</summary>
    public TimeSpan BackoffFor(int failures)
    {
        if (failures <= 0) return TimeSpan.Zero;
        var exponent = Math.Min(failures - 1, 30);
        var ticks = InitialBackoff.Ticks * Math.Pow(2, exponent);
        return ticks >= MaxBackoff.Ticks ? MaxBackoff : TimeSpan.FromTicks((long)ticks);
    }
}
