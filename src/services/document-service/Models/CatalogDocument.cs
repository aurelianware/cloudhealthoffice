using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using MongoDB.Bson.Serialization.Attributes;

namespace DocumentService.Models;

/// <summary>
/// One stored document and what it means: what it is, where it came from,
/// what it supports and who may see it. The bytes live in object storage
/// (<see cref="StorageContainer"/>/<see cref="StoragePath"/>, never returned to
/// callers); this record is the only way to reach them.
/// See docs/architecture/document-store.md §3.
/// </summary>
[BsonIgnoreExtraElements]
public sealed class CatalogDocument
{
    [BsonId]
    public string Id { get; set; } = string.Empty;

    public string TenantId { get; set; } = string.Empty;

    /// <summary>Optimistic-concurrency revision; every save increments it.</summary>
    public long Revision { get; set; }

    /// <summary>Content version of this document (1 for the first upload).</summary>
    public int DocumentVersion { get; set; } = 1;

    /// <summary>The document this one corrects, if it is a new version.</summary>
    public string? SupersedesId { get; set; }

    /// <summary>A value from <see cref="DocumentTypes"/> or a tenant extension.</summary>
    public string Type { get; set; } = string.Empty;

    public string? Title { get; set; }

    public string ContentType { get; set; } = string.Empty;

    public long SizeBytes { get; set; }

    /// <summary>SHA-256 of the content, lower-case hex. Duplicate uploads within a tenant share one document.</summary>
    public string? Sha256 { get; set; }

    public int? PageCount { get; set; }

    public DocumentSource Source { get; set; } = new();

    /// <summary>Every subject this document supports. Never empty.</summary>
    public List<DocumentLink> Links { get; set; } = new();

    /// <summary>Values from <see cref="SensitivityTags"/>.</summary>
    public List<string> Sensitivity { get; set; } = new();

    public DocumentRetention Retention { get; set; } = new();

    public DocumentScan Scan { get; set; } = new();

    public string StorageContainer { get; set; } = string.Empty;

    public string StoragePath { get; set; } = string.Empty;

    /// <summary>The staging object a pre-signed upload writes to; null once finalized.</summary>
    public string? PendingUploadPath { get; set; }

    public string CreatedBy { get; set; } = string.Empty;

    /// <summary>
    /// When a CHO service uploaded it for a user (attachment-service filing a
    /// provider's upload, say): that user. <see cref="CreatedBy"/> is then the service.
    /// </summary>
    public string? CreatedOnBehalfOf { get; set; }

    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// The front-door time (clearinghouse or portal receipt, fax arrival), not
    /// when CHO processed it. Clean-date and appeal clocks use this.
    /// </summary>
    public DateTime ReceivedAt { get; set; }
}

public sealed class DocumentSource
{
    /// <summary>A value from <see cref="DocumentChannels"/>.</summary>
    public string Channel { get; set; } = DocumentChannels.Upload;

    /// <summary>Identifiers from the channel (attachment control number, CDex task id, fax id...).</summary>
    public Dictionary<string, string> ExternalIds { get; set; } = new();
}

public sealed record DocumentLink
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    [BsonRepresentation(MongoDB.Bson.BsonType.String)]
    public DocumentLinkKind Kind { get; init; }

    public string Id { get; init; } = string.Empty;

    public override string ToString() => $"{Kind}:{Id}";
}

/// <summary>What a document can support. The read permission each needs is in <c>SubjectAccess</c>.</summary>
public enum DocumentLinkKind
{
    Member,
    Claim,
    ClaimLine,
    Authorization,
    Appeal,
    Grievance,
    Call,
    Provider,
    RfaiRequest,
    PaymentRun,
    /// <summary>A file-drop import run (claims-service <c>CpcdsDropRuns</c>): the source extracts it loaded.</summary>
    ImportRun,
    /// <summary>One generated state regulatory file (a Texas HHSC monthly text file): the file as it was handed over for submission.</summary>
    StateReport
}

public sealed class DocumentRetention
{
    public string PolicyId { get; set; } = "DEFAULT-10Y";

    public DateTime RetainUntil { get; set; }

    public LegalHold LegalHold { get; set; } = new();
}

public sealed class LegalHold
{
    public bool Set { get; set; }

    public string? By { get; set; }

    public DateTime? At { get; set; }

    public string? Reason { get; set; }

    public List<LegalHoldEvent> History { get; set; } = new();
}

public sealed class LegalHoldEvent
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    [BsonRepresentation(MongoDB.Bson.BsonType.String)]
    public LegalHoldAction Action { get; set; }

    public string Actor { get; set; } = string.Empty;

    public string? Reason { get; set; }

    public DateTime At { get; set; }
}

public enum LegalHoldAction
{
    Set,
    Released
}

public sealed class DocumentScan
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    [BsonRepresentation(MongoDB.Bson.BsonType.String)]
    public ScanStatus Status { get; set; } = ScanStatus.Pending;

    public string? Engine { get; set; }

    public DateTime? At { get; set; }
}

public enum ScanStatus
{
    /// <summary>Not scanned (yet). Served only where Documents:AllowUnscannedContent is on.</summary>
    Pending,
    Clean,
    /// <summary>Never served.</summary>
    Infected,
    /// <summary>The scan could not complete. Never served.</summary>
    Failed
}

/// <summary>The built-in document type taxonomy (§3). A tenant may use others.</summary>
public static class DocumentTypes
{
    /// <summary>
    /// A file a plan's core or UM system sent CHO (a CPCDS extract, an X12
    /// 278, ...), kept as received. The only type that may be CSV or X12, and
    /// the only one with its own size cap (<see cref="Services.DocumentUploadPolicy"/>).
    /// </summary>
    public const string SourceExtract = "SourceExtract";

    /// <summary>
    /// A file CHO generated for a state regulator (a Texas HHSC monthly
    /// appeals or complaints text file), kept as handed over for
    /// submission. Plain text or CSV only, and no other type may be.
    /// </summary>
    public const string StateSubmission = "StateSubmission";

    public static readonly IReadOnlySet<string> BuiltIn = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        // Clinical
        "MedicalRecord", "OperativeReport", "LabResult", "ImagingResult", "DischargeSummary",
        // Billing
        "ItemizedBill", "Invoice", "PriorPayerEob", "PaperClaimImage",
        // Authorization
        "ClinicalJustification", "DtrQuestionnaireResponse", "PasSupportingBundle",
        // Appeals and grievances
        "AppealRequest", "AppointmentOfRepresentative", "CaseFile", "DecisionLetter",
        // Correspondence
        "InboundLetter", "OutboundLetter", "Email",
        // Member
        "IdCard", "EnrollmentForm", "ConsentForm", "CoverageNotice",
        // Service
        "CallRecording", "CallTranscript", "CallSummary",
        // Data a plan's other systems sent (a core's CPCDS or X12 extract), as received
        SourceExtract,
        // A file CHO generated for a state regulator, as handed over
        StateSubmission,
        // Anything not yet classified
        "Other"
    };

    /// <summary>A type is one identifier: letters, digits, '-' and '_' (a tenant extension like "Tx-Form-2026").</summary>
    public static bool IsValid(string? type) =>
        !string.IsNullOrWhiteSpace(type)
        && type.Length <= 64
        && type.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}

/// <summary>Sensitivity tags (§3). Any tag needs documents:read-sensitive; PART2_SUD also needs a consent check.</summary>
public static class SensitivityTags
{
    public const string Part2Sud = "PART2_SUD";
    public const string BehavioralHealth = "BEHAVIORAL_HEALTH";
    public const string Hiv = "HIV";
    public const string Reproductive = "REPRODUCTIVE";
    public const string Genetic = "GENETIC";
    public const string MinorConfidential = "MINOR_CONFIDENTIAL";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Part2Sud, BehavioralHealth, Hiv, Reproductive, Genetic, MinorConfidential
    };
}

public static class DocumentChannels
{
    public const string Upload = "Upload";
    public const string X12_275 = "X12-275";
    public const string Fhir = "FHIR";
    public const string Fax = "Fax";
    public const string Mail = "Mail";
    public const string Call = "Call";
    public const string Generated = "Generated";

    /// <summary>A file the plan's system delivered by SFTP to the file drop.</summary>
    public const string Sftp = "SFTP";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        Upload, X12_275, Fhir, Fax, Mail, Call, Generated, Sftp
    };
}

/// <summary>
/// One entry in the append-only access audit (§5): who, which document (or
/// which subject's list), when, from where, and whether it was allowed.
/// Denied attempts are recorded too.
/// </summary>
[BsonIgnoreExtraElements]
public sealed class DocumentAccessEvent
{
    [BsonId]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string TenantId { get; set; } = string.Empty;

    /// <summary>Null for a list of a subject's documents.</summary>
    public string? DocumentId { get; set; }

    public string Actor { get; set; } = string.Empty;

    /// <summary>The user a CHO service acted for (its X-CHO-On-Behalf-Of header); null when the actor is the user.</summary>
    public string? OnBehalfOf { get; set; }

    public DateTime At { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    [BsonRepresentation(MongoDB.Bson.BsonType.String)]
    public DocumentAccessAction Action { get; set; }

    public string? SubjectKind { get; set; }

    public string? SubjectId { get; set; }

    /// <summary>The screen the request came from, when the caller says (X-CHO-Screen header).</summary>
    public string? Screen { get; set; }

    public bool Allowed { get; set; }

    /// <summary>Why it was denied, or what a list withheld.</summary>
    public string? Detail { get; set; }
}

public enum DocumentAccessAction
{
    List,
    View,
    Download,
    Upload,
    Link,
    Unlink,
    LegalHold,
    AccessLog,
    ScanRetry
}

// ── API shapes ────────────────────────────────────────────────────────────

/// <summary>A document as callers see it: no storage location, and only the links the caller may read.</summary>
public sealed class DocumentView
{
    public string Id { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public string? Title { get; init; }
    public string ContentType { get; init; } = string.Empty;
    public long SizeBytes { get; init; }
    public string? Sha256 { get; init; }
    public int DocumentVersion { get; init; }
    public string? SupersedesId { get; init; }
    public DocumentSource Source { get; init; } = new();
    public IReadOnlyList<DocumentLink> Links { get; init; } = [];
    /// <summary>Links to subjects the caller may not read: counted, not shown.</summary>
    public int OtherLinks { get; init; }
    public IReadOnlyList<string> Sensitivity { get; init; } = [];
    public string RetentionPolicyId { get; init; } = string.Empty;
    public DateTime RetainUntil { get; init; }
    public bool LegalHold { get; init; }
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ScanStatus ScanStatus { get; init; }
    public bool UploadPending { get; init; }
    public DateTime ReceivedAt { get; init; }
    public DateTime CreatedAt { get; init; }
    public string CreatedBy { get; init; } = string.Empty;
    public string? CreatedOnBehalfOf { get; init; }
}

public sealed class DocumentListResponse
{
    public IReadOnlyList<DocumentView> Documents { get; init; } = [];

    /// <summary>Documents on this subject the caller may not see (sensitivity). Counted so nobody mistakes a gap for "nothing on file".</summary>
    public int Withheld { get; init; }
}

public sealed class UploadDocumentForm
{
    [Required]
    public string Type { get; set; } = string.Empty;

    public string? Title { get; set; }

    /// <summary>Subjects as "Kind:Id" (e.g. "Claim:CLM-1"). At least one.</summary>
    public List<string> Links { get; set; } = new();

    public List<string> Sensitivity { get; set; } = new();

    public string? SourceChannel { get; set; }

    /// <summary>Identifiers from the channel as "key=value" (e.g. "AttachmentControlNumber=ACN-1"). At most 10.</summary>
    public List<string> ExternalIds { get; set; } = new();

    public DateTime? ReceivedAt { get; set; }

    public string? StateCode { get; set; }

    /// <summary>When retention runs from coverage end (member documents): the member's coverage termination date.</summary>
    public DateTime? CoverageTerminationDate { get; set; }

    public string? RetentionPolicyId { get; set; }

    /// <summary>Stores the document under a legal hold from the start. Needs records:legal-hold; never merged into an existing copy.</summary>
    public bool LegalHold { get; set; }

    public string? LegalHoldReason { get; set; }
}

public sealed class PresignedUploadRequest
{
    [Required]
    public string Type { get; set; } = string.Empty;

    public string? Title { get; set; }

    [Required]
    public string ContentType { get; set; } = string.Empty;

    public string? FileName { get; set; }

    public List<string> Links { get; set; } = new();

    public List<string> Sensitivity { get; set; } = new();

    public string? SourceChannel { get; set; }

    public DateTime? ReceivedAt { get; set; }

    public string? StateCode { get; set; }

    /// <summary>When retention runs from coverage end (member documents): the member's coverage termination date.</summary>
    public DateTime? CoverageTerminationDate { get; set; }

    public string? RetentionPolicyId { get; set; }

    /// <summary>Stores the document under a legal hold from the start. Needs records:legal-hold; never merged into an existing copy.</summary>
    public bool LegalHold { get; set; }

    public string? LegalHoldReason { get; set; }
}

public sealed class PresignedUploadResponse
{
    public string DocumentId { get; init; } = string.Empty;
    public string UploadUrl { get; init; } = string.Empty;
    public string UploadMethod { get; init; } = "PUT";
    public Dictionary<string, string> UploadHeaders { get; init; } = new();
    public DateTime ExpiresAtUtc { get; init; }
}

public sealed class LinkRequest
{
    [Required]
    public string Link { get; set; } = string.Empty;
}

public sealed class LegalHoldRequest
{
    public const int MaxReasonLength = 500;

    public bool LegalHold { get; set; }

    public string? Reason { get; set; }
}
