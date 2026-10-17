namespace CloudHealthOffice.Portal.Services;

public class MassAdjudicationRunSummary
{
    public string Id { get; set; } = string.Empty;
    public string Status { get; set; } = "Completed";
    public MassAdjudicationRunMetadata Run { get; set; } = new();
    public int TotalClaims { get; set; }
    public int Processed { get; set; }
    public int Paid { get; set; }
    public int Pended { get; set; }
    public int BusinessDenials { get; set; }
    public int ObservationTimeouts { get; set; }
    public int PlatformFailures { get; set; }
    public int ServiceBusObservationTimeouts { get; set; }
    public int ServiceBusLateCompletions { get; set; }
    public int ServiceBusUnreconciledClaims { get; set; }
    public int WorkflowScenarios { get; set; }
    public int WorkflowMatches { get; set; }
    public int WorkflowMismatches { get; set; }
    public int WorkflowUnsupported { get; set; }
    public int WorkflowObservationTimeouts { get; set; }
    public TimeSpan Elapsed { get; set; }
    public double ThroughputClaimsPerSecond { get; set; }
    public double P95LatencyMilliseconds { get; set; }
    public double P99LatencyMilliseconds { get; set; }
    public MassAdjudicationStageTiming? SubmitTiming { get; set; }
    public MassAdjudicationStageTiming? AdjudicateTiming { get; set; }
    public MassAdjudicationStageTiming? WritebackTiming { get; set; }
    public List<MassAdjudicationStageTiming> AdjudicationStepTimings { get; set; } = new();
    public List<MassAdjudicationLifecycleTiming> LifecycleTimings { get; set; } = new();
    public MassAdjudicationFixturePreparation? FixturePreparation { get; set; }
    public decimal? AveragePaymentDelta { get; set; }
    public decimal PaymentTolerance { get; set; }
    public int PaymentComparisons { get; set; }
    public int PaymentMatches { get; set; }
    public int PaymentMismatches { get; set; }
    public decimal? MaximumPaymentDelta { get; set; }
    public List<MassAdjudicationPaymentDeltaBucket> PaymentDeltaDistribution { get; set; } = new();
    public List<MassAdjudicationBusinessDenialSummary> BusinessDenialBreakdown { get; set; } = new();
    public List<MassAdjudicationWorkflowScenarioSummary> WorkflowScenarioBreakdown { get; set; } = new();
    public List<MassAdjudicationFailureSummary> SampleFailures { get; set; } = new();
    public List<MassAdjudicationClaimResult> ClaimResults { get; set; } = new();
    public DateTime CreatedAtUtc { get; set; }
    public DateTime LastUpdatedAtUtc { get; set; }
    public MassAdjudicationRunProgress? Progress { get; set; }
}
