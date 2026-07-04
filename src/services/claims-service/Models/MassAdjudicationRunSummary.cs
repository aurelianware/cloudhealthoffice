using MongoDB.Bson.Serialization.Attributes;

namespace ClaimsService.Models;

public class MassAdjudicationRunSummary
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public MassAdjudicationRunMetadata Run { get; set; } = new();
    public int TotalClaims { get; set; }
    public int Processed { get; set; }
    public int Paid { get; set; }
    public int BusinessDenials { get; set; }
    public int PlatformFailures { get; set; }
    public TimeSpan Elapsed { get; set; }
    public double ThroughputClaimsPerSecond { get; set; }
    public double P95LatencyMilliseconds { get; set; }
    public double P99LatencyMilliseconds { get; set; }
    public MassAdjudicationStageTiming? SubmitTiming { get; set; }
    public MassAdjudicationStageTiming? AdjudicateTiming { get; set; }
    public MassAdjudicationStageTiming? WritebackTiming { get; set; }
    public decimal? AveragePaymentDelta { get; set; }
    public List<MassAdjudicationBusinessDenialSummary> BusinessDenialBreakdown { get; set; } = new();
    public List<MassAdjudicationFailureSummary> SampleFailures { get; set; } = new();
    [BsonIgnore]
    public List<MassAdjudicationClaimResult> ClaimResults { get; set; } = new();
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

public class MassAdjudicationRunMetadata
{
    public string TenantId { get; set; } = string.Empty;
    public int RequestedClaims { get; set; }
    public int Seed { get; set; }
    public int Parallelism { get; set; }
    public string ClaimsUrl { get; set; } = string.Empty;
    public string BenefitUrl { get; set; } = string.Empty;
    public string ProviderUrl { get; set; } = string.Empty;
    public bool SeedProviders { get; set; }
    public bool SkipClaimUpdate { get; set; }
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset CompletedAtUtc { get; set; }
}

public class MassAdjudicationStageTiming
{
    public string Label { get; set; } = string.Empty;
    public double AverageMilliseconds { get; set; }
    public double P95Milliseconds { get; set; }
}

public class MassAdjudicationBusinessDenialSummary
{
    public string Code { get; set; } = string.Empty;
    public int Count { get; set; }
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
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
