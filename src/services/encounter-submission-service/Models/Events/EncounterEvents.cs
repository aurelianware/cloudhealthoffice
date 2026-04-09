using System.Text.Json.Serialization;

namespace EncounterSubmissionService.Models.Events;

/// <summary>
/// Published to the <c>encounter-submission-created</c> Kafka topic when
/// a new encounter submission record is created from an adjudication-completed event.
/// Consumed by downstream analytics and compliance dashboards.
/// </summary>
public class EncounterSubmissionCreatedEvent
{
    [JsonPropertyName("submissionId")]
    public string SubmissionId { get; set; } = string.Empty;

    [JsonPropertyName("claimId")]
    public string ClaimId { get; set; } = string.Empty;

    [JsonPropertyName("tenantId")]
    public string TenantId { get; set; } = string.Empty;

    [JsonPropertyName("deadline")]
    public DateTime Deadline { get; set; }

    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Published to the <c>encounter-deadline-warning</c> Kafka topic when a
/// submission is within 7 days (configurable) of its 60-day FMMIS deadline.
/// Consumed by operations alerts and escalation workflows.
/// </summary>
public class EncounterDeadlineWarningEvent
{
    [JsonPropertyName("submissionId")]
    public string SubmissionId { get; set; } = string.Empty;

    [JsonPropertyName("claimId")]
    public string ClaimId { get; set; } = string.Empty;

    [JsonPropertyName("tenantId")]
    public string TenantId { get; set; } = string.Empty;

    [JsonPropertyName("deadline")]
    public DateTime Deadline { get; set; }

    [JsonPropertyName("daysRemaining")]
    public double DaysRemaining { get; set; }

    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Published to the <c>encounter-submission-failed</c> Kafka topic when
/// a submission is rejected by FMMIS or fails during batch processing.
/// Consumed by operations alerts and retry/escalation workflows.
/// </summary>
public class EncounterSubmissionFailedEvent
{
    [JsonPropertyName("submissionId")]
    public string SubmissionId { get; set; } = string.Empty;

    [JsonPropertyName("claimId")]
    public string ClaimId { get; set; } = string.Empty;

    [JsonPropertyName("tenantId")]
    public string TenantId { get; set; } = string.Empty;

    [JsonPropertyName("errorMessage")]
    public string ErrorMessage { get; set; } = string.Empty;

    [JsonPropertyName("retryCount")]
    public int RetryCount { get; set; }

    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}
