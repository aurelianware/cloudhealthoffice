using MongoDB.Bson.Serialization.Attributes;

namespace ClaimsService.Models;

public class MassAdjudicationRunSummary
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
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
    [BsonIgnore]
    public List<MassAdjudicationClaimResult> ClaimResults { get; set; } = new();
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime LastUpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public MassAdjudicationRunProgress? Progress { get; set; }
}

public class MassAdjudicationRunMetadata
{
    public string TenantId { get; set; } = string.Empty;
    public int RequestedClaims { get; set; }
    public int Seed { get; set; }
    public int Parallelism { get; set; }
    public string ClaimsUrl { get; set; } = string.Empty;
    public string BenefitUrl { get; set; } = string.Empty;
    public string MemberUrl { get; set; } = string.Empty;
    public string CoverageUrl { get; set; } = string.Empty;
    public string ProviderUrl { get; set; } = string.Empty;
    public bool SeedMembers { get; set; }
    public bool SeedProviders { get; set; }
    public bool SkipClaimUpdate { get; set; }
    public int LineOfBusiness { get; set; }
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset CompletedAtUtc { get; set; }
}

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

public class MassAdjudicationStageTiming
{
    public string Label { get; set; } = string.Empty;
    public double AverageMilliseconds { get; set; }
    public double P95Milliseconds { get; set; }
}

public class MassAdjudicationLifecycleTiming
{
    public string Label { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public double DurationMilliseconds { get; set; }
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset CompletedAtUtc { get; set; }
}

public class MassAdjudicationFixturePreparation
{
    public int GeneratedClaims { get; set; }
    public int ProviderPoolDistinctBefore { get; set; }
    public int ProviderPoolDistinctAfter { get; set; }
    public int ProviderPoolReusedAssignments { get; set; }
    public int ProviderPoolProtectedClaims { get; set; }
    public int MembersCreated { get; set; }
    public int MembersExisting { get; set; }
    public int MemberStatusesAligned { get; set; }
    public int CobCoverageCreated { get; set; }
    public int CobCoverageExisting { get; set; }
    public int ProviderNetworksCreated { get; set; }
    public int ProviderNetworksExisting { get; set; }
    public int ProvidersCreated { get; set; }
    public int ProvidersExisting { get; set; }
}

public class MassAdjudicationPaymentDeltaBucket
{
    public string Label { get; set; } = string.Empty;
    public decimal? LowerBoundExclusive { get; set; }
    public decimal? UpperBoundInclusive { get; set; }
    public int Count { get; set; }
}

public class MassAdjudicationBusinessDenialSummary
{
    public string Code { get; set; } = string.Empty;
    public int Count { get; set; }
}

public class MassAdjudicationWorkflowScenarioSummary
{
    public string Scenario { get; set; } = string.Empty;
    public int Total { get; set; }
    public int Matches { get; set; }
    public int Mismatches { get; set; }
    public int Unsupported { get; set; }
    public int ObservationTimeouts { get; set; }
    public int Unspecified { get; set; }
}

public class MassAdjudicationFailureSummary
{
    public string GeneratedClaimId { get; set; } = string.Empty;
    public string? Stage { get; set; }
    public string? Error { get; set; }
}

public class MassAdjudicationClaimResult
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string RunId { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string GeneratedClaimId { get; set; } = string.Empty;
    public string? SubmittedClaimId { get; set; }
    public string ClaimType { get; set; } = string.Empty;
    public string? ValidationScenario { get; set; }
    public string? ExpectedOutcome { get; set; }
    public string? ExpectedBusinessDenialCode { get; set; }
    public string ValidationStatus { get; set; } = "Unspecified";
    public string Outcome { get; set; } = string.Empty;
    public bool AdjudicationSuccess { get; set; }
    public string? BusinessDenialCode { get; set; }
    public string? FailureStage { get; set; }
    public string? Error { get; set; }
    public decimal? ActualPlanPayment { get; set; }
    public decimal? ExpectedPlanPayment { get; set; }
    public decimal? PaymentDelta { get; set; }
    public double ElapsedMilliseconds { get; set; }
    public double SubmitMilliseconds { get; set; }
    public double AdjudicationMilliseconds { get; set; }
    public double WritebackMilliseconds { get; set; }
    public Dictionary<string, double> AdjudicationStepMilliseconds { get; set; } = new();
    public bool ServiceBusObservationTimedOut { get; set; }
    public bool ReconciledAfterObservationTimeout { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
