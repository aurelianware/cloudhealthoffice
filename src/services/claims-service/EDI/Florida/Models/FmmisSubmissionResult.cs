namespace ClaimsService.EDI.Florida.Models;

/// <summary>
/// Represents a single FMMIS-compliant 837 transaction produced by
/// <see cref="FmmisClaimTransformer"/>. Contains the raw EDI string
/// along with metadata needed for file packaging and submission tracking.
/// </summary>
public class FmmisTransaction
{
    /// <summary>
    /// Payer-assigned claim number (CLM01).
    /// </summary>
    public string ClaimNumber { get; set; } = string.Empty;

    /// <summary>
    /// ISA13 interchange control number assigned during transformation.
    /// </summary>
    public string InterchangeControlNumber { get; set; } = string.Empty;

    /// <summary>
    /// Tenant that owns this claim.
    /// </summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>
    /// FMMIS submitter ID (ISA06) used in the ISA envelope.
    /// </summary>
    public string SubmitterId { get; set; } = string.Empty;

    /// <summary>
    /// Transaction type: "837P" (Professional) or "837I" (Institutional).
    /// </summary>
    public string TransactionType { get; set; } = string.Empty;

    /// <summary>
    /// The complete FMMIS-compliant X12 837 EDI string, ready for
    /// packaging into a submission file.
    /// </summary>
    public string RawEdi { get; set; } = string.Empty;

    /// <summary>
    /// UTC timestamp when the transformation was performed.
    /// </summary>
    public DateTime TransformedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// The member's Medicaid ID used in NM109 of the 2000B subscriber loop.
    /// </summary>
    public string MedicaidId { get; set; } = string.Empty;

    /// <summary>
    /// The billing provider's FL Medicaid Provider Number placed in REF*1D.
    /// </summary>
    public string FloridaMedicaidProviderId { get; set; } = string.Empty;
}

/// <summary>
/// Physical file produced by <see cref="FmmisFileBuilder"/>. Contains the
/// assembled EDI content (ISA/GS/.../GE/IEA) as a byte array ready for
/// SFTP transmission to FMMIS.
/// </summary>
public class FmmisSubmissionFile
{
    /// <summary>
    /// FMMIS file name following the required convention:
    /// <c>FMMIS.{SubmitterId}.{yyyyMMdd_HHmmss}.dat</c>
    /// </summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>
    /// UTF-8 encoded EDI content ready for transmission.
    /// </summary>
    public byte[] Content { get; set; } = Array.Empty<byte>();

    /// <summary>
    /// Total number of ST/SE transaction sets included in this file.
    /// </summary>
    public int TransactionCount { get; set; }

    /// <summary>
    /// Claim numbers (CLM01) for every transaction in this file.
    /// </summary>
    public List<string> ClaimIds { get; set; } = new();
}

/// <summary>
/// Tracking record for an FMMIS submission. Persisted after a file is
/// transmitted so the 999 response can be correlated back to the original batch.
/// </summary>
public class FmmisSubmissionResult
{
    /// <summary>
    /// Unique submission identifier.
    /// </summary>
    public Guid SubmissionId { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Tenant that submitted the file.
    /// </summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>
    /// FMMIS file name (<c>FMMIS.{SubmitterId}.{yyyyMMdd_HHmmss}.dat</c>).
    /// </summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>
    /// UTC timestamp when the file was submitted to FMMIS.
    /// </summary>
    public DateTime SubmittedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Number of ST/SE transaction sets in the submitted file.
    /// </summary>
    public int TransactionCount { get; set; }

    /// <summary>
    /// Claim numbers (CLM01) for every transaction in the submission.
    /// </summary>
    public List<string> ClaimIds { get; set; } = new();

    /// <summary>
    /// Current status of this submission in the FMMIS acknowledgment lifecycle.
    /// </summary>
    public FmmisSubmissionStatus Status { get; set; } = FmmisSubmissionStatus.Pending;

    /// <summary>
    /// Acknowledgment code returned in the FMMIS 999 response
    /// (e.g., "A" accepted, "R" rejected). Null until a 999 is received.
    /// </summary>
    public string? AcknowledgmentCode { get; set; }

    /// <summary>
    /// Error messages from the FMMIS 999 response or from transmission failures.
    /// Empty if the submission was accepted.
    /// </summary>
    public List<string> Errors { get; set; } = new();
}

/// <summary>
/// Lifecycle status of an FMMIS encounter submission, tracked from
/// initial file generation through 999 acknowledgment processing.
/// </summary>
public enum FmmisSubmissionStatus
{
    /// <summary>
    /// File generated and transmitted; awaiting FMMIS acknowledgment.
    /// </summary>
    Pending,

    /// <summary>
    /// FMMIS 999 received with status A — all transactions accepted.
    /// </summary>
    Accepted,

    /// <summary>
    /// FMMIS 999 received with status E — some transactions accepted,
    /// others rejected. Check <see cref="FmmisSubmissionResult.Errors"/>.
    /// </summary>
    PartialAccept,

    /// <summary>
    /// FMMIS 999 received with status R — entire file rejected.
    /// </summary>
    Rejected
}
