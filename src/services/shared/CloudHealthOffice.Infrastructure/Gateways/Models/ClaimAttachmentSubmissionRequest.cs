namespace CloudHealthOffice.Infrastructure.Gateways.Models;

/// <summary>
/// Vendor-neutral request to associate supporting documentation with an
/// existing claim transmission and submit it as a 275-equivalent attachment.
/// Content bytes are not on this object — only a secure content reference.
/// </summary>
public sealed class ClaimAttachmentSubmissionRequest
{
    public string TenantId { get; set; } = string.Empty;

    public string ClaimId { get; set; } = string.Empty;

    public string TransmissionId { get; set; } = string.Empty;

    public string? PayerId { get; set; }

    public string AttachmentId { get; set; } = string.Empty;

    /// <summary>
    /// Caller-visible attachment control number. Distinct from storage keys.
    /// </summary>
    public string? AttachmentControlNumber { get; set; }

    public ClaimAttachmentType AttachmentType { get; set; } = ClaimAttachmentType.Other;

    public ClaimAttachmentMode Mode { get; set; } = ClaimAttachmentMode.Unsolicited;

    /// <summary>
    /// Vendor-neutral payer request / RFAI control number for solicited
    /// attachments. Stedi's documented APIs support unsolicited 275 only.
    /// </summary>
    public string? PayerRequestControlNumber { get; set; }

    /// <summary>Untrusted display name. Never used as a storage path or URL.</summary>
    public string? FileName { get; set; }

    public string ContentType { get; set; } = string.Empty;

    public long ContentLength { get; set; }

    public ClaimAttachmentContentReference? Content { get; set; }

    public int? ServiceLineNumber { get; set; }

    public string? Description { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public string? CorrelationId { get; set; }

    /// <summary>
    /// Attachment version. A changed checksum with the same
    /// <see cref="AttachmentId"/> requires a new version — submitted content
    /// is immutable.
    /// </summary>
    public int AttachmentVersion { get; set; } = 1;

    public string ResolveIdempotencyKey()
    {
        var line = ServiceLineNumber.HasValue ? ServiceLineNumber.Value.ToString() : "claim";
        var checksum = Content?.ChecksumSha256 ?? string.Empty;
        return $"{TenantId}|{TransmissionId}|{AttachmentId}|{checksum}|{AttachmentType}|{line}|{AttachmentVersion}";
    }

    public bool IsClaimLevel => !ServiceLineNumber.HasValue;

    public ClaimAttachmentAssociationLevel AssociationLevel =>
        ServiceLineNumber.HasValue
            ? ClaimAttachmentAssociationLevel.ServiceLine
            : ClaimAttachmentAssociationLevel.Claim;
}

/// <summary>Secure pointer to attachment bytes. Never holds the payload.</summary>
public sealed class ClaimAttachmentContentReference
{
    public string Container { get; set; } = string.Empty;

    /// <summary>Storage key. Distinct from the caller display name.</summary>
    public string StorageKey { get; set; } = string.Empty;

    public string ContentType { get; set; } = string.Empty;

    public long ContentLength { get; set; }

    public string ChecksumSha256 { get; set; } = string.Empty;

    public ClaimAttachmentScanStatus ScanStatus { get; set; } = ClaimAttachmentScanStatus.Unknown;

    public string? DisplayName { get; set; }
}

public sealed class ClaimAttachmentSubmissionResult
{
    public string AttachmentId { get; set; } = string.Empty;

    public string AttachmentTransmissionId { get; set; } = string.Empty;

    public string TransmissionId { get; set; } = string.Empty;

    public string ClaimId { get; set; } = string.Empty;

    public ClaimAttachmentTransmissionStatus Status { get; set; }

    public ClaimAttachmentType AttachmentType { get; set; }

    public ClaimAttachmentMode Mode { get; set; }

    public ClaimAttachmentAssociationLevel AssociationLevel { get; set; }

    public int? ServiceLineNumber { get; set; }

    public string ContentType { get; set; } = string.Empty;

    public long ContentLength { get; set; }

    public string? ChecksumSha256 { get; set; }

    public string? ExternalTransactionId { get; set; }

    public string? AttachmentControlNumber { get; set; }

    public string IdempotencyKey { get; set; } = string.Empty;

    /// <summary>
    /// True when the gateway accepted the attachment for processing. This is
    /// not payer review, claim acceptance, adjudication, or payment.
    /// </summary>
    public bool AcceptedForProcessing { get; set; }

    public bool ReplayOfExistingTransmission { get; set; }
}

public enum ClaimAttachmentType
{
    MedicalRecord = 1,
    ClinicalNote = 2,
    OperativeReport = 3,
    DiagnosticImage = 4,
    LabResult = 5,
    Referral = 6,
    AuthorizationDocumentation = 7,
    DentalImage = 8,
    DentalNarrative = 9,
    Radiograph = 10,
    IntraoralImage = 11,
    PeriodontalChart = 12,
    TreatmentPlan = 13,
    Other = 99
}

public enum ClaimAttachmentMode
{
    Unsolicited = 1,
    Solicited = 2
}

public enum ClaimAttachmentAssociationLevel
{
    None = 0,
    Claim = 1,
    ServiceLine = 2
}

public enum ClaimAttachmentScanStatus
{
    Unknown = 0,
    Safe = 1,
    Quarantined = 2,
    Unsafe = 3,
    ScanFailed = 4
}

/// <summary>
/// Attachment-transmission lifecycle. Independent of 837, 277CA,
/// adjudication, and payment state.
/// </summary>
public enum ClaimAttachmentTransmissionStatus
{
    Stored = 1,
    Validated = 2,
    ReadyForSubmission = 3,
    Transmitting = 4,
    Submitted = 5,
    GatewayAccepted = 6,
    GatewayRejected = 7,
    Failed = 8
}

public static class ClaimAttachmentTransmissionStatuses
{
    public static bool PreventsDuplicateSubmit(ClaimAttachmentTransmissionStatus status) =>
        status is ClaimAttachmentTransmissionStatus.Submitted
            or ClaimAttachmentTransmissionStatus.GatewayAccepted;
}
