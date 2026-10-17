namespace CloudHealthOffice.Portal.Services;

public class MassAdjudicationRunProgress
{
    public string Phase { get; set; } = "Processing claims";
    public int RequestedClaims { get; set; }
    public int CompletedClaims { get; set; }
    public int ProcessedClaims { get; set; }
    public int PlatformFailures { get; set; }
    public double PercentComplete { get; set; }
    public double CurrentThroughputClaimsPerSecond { get; set; }
    public double RollingP95LatencyMilliseconds { get; set; }
    public double RollingP99LatencyMilliseconds { get; set; }
    public int PendingExpectedPendObservations { get; set; }
    public int PendingTerminalStatusObservations { get; set; }
    public int PendingWorkflowObservations { get; set; }
    public DateTimeOffset LastPublishedAtUtc { get; set; }
}
