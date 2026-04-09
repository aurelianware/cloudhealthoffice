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
/// Aggregated result of an FMMIS batch submission, returned by
/// <see cref="FmmisFileBuilder"/> after packaging one or more
/// <see cref="FmmisTransaction"/> instances into a submission file.
/// </summary>
public class FmmisSubmissionResult
{
    /// <summary>
    /// FMMIS file name following the required convention:
    /// FMMIS.{SubmitterId}.{yyyyMMdd_HHmmss}.dat
    /// </summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>
    /// Total number of transactions included in the file.
    /// </summary>
    public int TransactionCount { get; set; }

    /// <summary>
    /// Total number of claim lines across all transactions.
    /// </summary>
    public int TotalClaimLines { get; set; }

    /// <summary>
    /// Interchange control number (ISA13) for the batch envelope.
    /// </summary>
    public string InterchangeControlNumber { get; set; } = string.Empty;

    /// <summary>
    /// Submitter ID (ISA06) used in the batch envelope.
    /// </summary>
    public string SubmitterId { get; set; } = string.Empty;

    /// <summary>
    /// UTC timestamp when the file was generated.
    /// </summary>
    public DateTime GeneratedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Individual transactions included in this submission.
    /// </summary>
    public List<FmmisTransaction> Transactions { get; set; } = new();
}
