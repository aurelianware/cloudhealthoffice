using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace EncounterSubmissionService.Models;

/// <summary>
/// Cosmos DB document tracking the 60-day AHCA encounter submission window
/// for a single adjudicated FL Medicaid claim. Partition key: <see cref="TenantId"/>.
/// Created automatically when the adjudication-completed Kafka event is received.
/// </summary>
public class EncounterSubmission
{
    /// <summary>
    /// Unique document identifier (Cosmos DB document id).
    /// </summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Multi-tenant partition key (required for Cosmos DB isolation).
    /// </summary>
    [JsonPropertyName("tenantId")]
    [Required]
    public string TenantId { get; set; } = string.Empty;

    /// <summary>
    /// The adjudicated claim ID that this encounter submission tracks.
    /// </summary>
    [JsonPropertyName("claimId")]
    [Required]
    public string ClaimId { get; set; } = string.Empty;

    /// <summary>
    /// UTC timestamp when the claim was adjudicated.
    /// The 60-day submission window starts from this date.
    /// </summary>
    [JsonPropertyName("claimAdjudicatedAt")]
    [Required]
    public DateTime ClaimAdjudicatedAt { get; set; }

    /// <summary>
    /// Two-letter state code (e.g. "FL") for the regulatory jurisdiction.
    /// </summary>
    [JsonPropertyName("stateCode")]
    [Required]
    [StringLength(2)]
    public string StateCode { get; set; } = "FL";

    /// <summary>
    /// Calculated deadline by which the encounter must be submitted to FMMIS.
    /// Equals <see cref="ClaimAdjudicatedAt"/> + 60 calendar days.
    /// </summary>
    [JsonPropertyName("submissionDeadline")]
    public DateTime SubmissionDeadline { get; set; }

    /// <summary>
    /// Current lifecycle status of this encounter submission.
    /// </summary>
    [JsonPropertyName("status")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public EncounterSubmissionStatus Status { get; set; } = EncounterSubmissionStatus.Pending;

    /// <summary>
    /// FMMIS batch file ID that this encounter was included in.
    /// Null until the encounter is batched.
    /// </summary>
    [JsonPropertyName("batchId")]
    public string? BatchId { get; set; }

    /// <summary>
    /// UTC timestamp when the batch was transmitted to FMMIS.
    /// </summary>
    [JsonPropertyName("submittedAt")]
    public DateTime? SubmittedAt { get; set; }

    /// <summary>
    /// UTC timestamp when the FMMIS 999 acknowledgment was received.
    /// </summary>
    [JsonPropertyName("acknowledgedAt")]
    public DateTime? AcknowledgedAt { get; set; }

    /// <summary>
    /// Acknowledgment code from the FMMIS 999 response
    /// (e.g., "A" accepted, "E" accepted with errors, "R" rejected).
    /// </summary>
    [JsonPropertyName("acknowledgmentCode")]
    public string? AcknowledgmentCode { get; set; }

    /// <summary>
    /// Number of times this encounter has been re-submitted after rejection.
    /// </summary>
    [JsonPropertyName("retryCount")]
    public int RetryCount { get; set; }

    /// <summary>
    /// Most recent error message (from validation, transmission, or 999 rejection).
    /// </summary>
    [JsonPropertyName("lastError")]
    public string? LastError { get; set; }

    /// <summary>
    /// Audit: document creation timestamp.
    /// </summary>
    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Audit: last modification timestamp.
    /// </summary>
    [JsonPropertyName("updatedAt")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
