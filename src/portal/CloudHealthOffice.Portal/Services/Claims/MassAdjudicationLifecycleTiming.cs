namespace CloudHealthOffice.Portal.Services;

public class MassAdjudicationLifecycleTiming
{
    public string Label { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public double DurationMilliseconds { get; set; }
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset CompletedAtUtc { get; set; }
}
