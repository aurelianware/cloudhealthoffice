using System.Text.Json.Serialization;

namespace CloudHealthOffice.Portal.Services;

public class MassAdjudicationClaimResult
{
    public string Id { get; set; } = string.Empty;
    public string RunId { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string GeneratedClaimId { get; set; } = string.Empty;
    public string? SubmittedClaimId { get; set; }
    [JsonConverter(typeof(FlexibleClaimTypeJsonConverter))]
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
    public DateTime CreatedAtUtc { get; set; }
}
