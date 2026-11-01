using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace EncounterSubmissionService.Models;

/// <summary>
/// Document tracking one adjudicated Medicaid claim through state encounter
/// submission: Florida (FMMIS), Texas (TMHP) or California (DHCS). Partition key:
/// <see cref="TenantId"/>.
/// Created automatically when the adjudication-completed Kafka event is received.
/// </summary>
public class EncounterSubmission
{
    /// <summary>
    /// Unique document identifier.
    /// </summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Owning tenant; every read and write filters on it.
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
    /// Deadline by which the encounter must reach the state, computed by the
    /// state's route (Florida: adjudication + 60 days; Texas and California:
    /// days after the end of the adjudication month).
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
    /// Receiver the record is routed to (FMMIS, TMHP, DHCS). Null on records created
    /// before state routes existed, which are all Florida.
    /// </summary>
    [JsonPropertyName("receiver")]
    public string? Receiver { get; set; }

    /// <summary>837 form the encounter was written as (Professional, Institutional).</summary>
    [JsonPropertyName("formType")]
    public string? FormType { get; set; }

    /// <summary>Staged file the encounter was written to.</summary>
    [JsonPropertyName("fileName")]
    public string? FileName { get; set; }

    /// <summary>ISA13 of the interchange that carried the encounter (X12 routes).</summary>
    [JsonPropertyName("interchangeControlNumber")]
    public string? InterchangeControlNumber { get; set; }

    /// <summary>GS06 of the functional group; a 999 AK1 refers to it.</summary>
    [JsonPropertyName("groupControlNumber")]
    public string? GroupControlNumber { get; set; }

    /// <summary>ST02 of the encounter's own transaction set; a 999 AK2/IK2 refers to it.</summary>
    [JsonPropertyName("transactionSetControlNumber")]
    public string? TransactionSetControlNumber { get; set; }

    /// <summary>CLM01 as sent; a 277CA and state response files echo it.</summary>
    [JsonPropertyName("patientControlNumber")]
    public string? PatientControlNumber { get; set; }

    /// <summary>
    /// The state's identifier for this encounter (277CA REF*1K: TMHP ICN, DHCS claim ID).
    /// A replacement or void must reference it in REF*F8.
    /// </summary>
    [JsonPropertyName("stateClaimReference")]
    public string? StateClaimReference { get; set; }

    /// <summary>Status codes from the most recent state response (999 IK5, 277CA STC).</summary>
    [JsonPropertyName("stateStatusCodes")]
    public List<string> StateStatusCodes { get; set; } = new();

    /// <summary>Every state response applied to this record, oldest first.</summary>
    [JsonPropertyName("responseHistory")]
    public List<EncounterResponseEvent> ResponseHistory { get; set; } = new();

    /// <summary>Pre-submission validation failures that held the encounter back.</summary>
    [JsonPropertyName("validationErrors")]
    public List<string> ValidationErrors { get; set; } = new();

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

/// <summary>One state response applied to an encounter submission.</summary>
public class EncounterResponseEvent
{
    [JsonPropertyName("responseId")]
    public string ResponseId { get; set; } = string.Empty;

    /// <summary>999, 277CA, 277.</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = string.Empty;

    [JsonPropertyName("receivedAt")]
    public DateTime ReceivedAt { get; set; }

    [JsonPropertyName("statusCodes")]
    public List<string> StatusCodes { get; set; } = new();

    [JsonPropertyName("resultingStatus")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public EncounterSubmissionStatus ResultingStatus { get; set; }

    [JsonPropertyName("errors")]
    public List<string> Errors { get; set; } = new();
}
