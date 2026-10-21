using MongoDB.Bson.Serialization.Attributes;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace MemberDocumentService.Models;

[BsonIgnoreExtraElements]
public class MemberDocument
{
    [Required]
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [Required]
    [StringLength(100)]
    public string TenantId { get; set; } = string.Empty;

    [Required]
    [StringLength(100)]
    public string MemberId { get; set; } = string.Empty;

    [Required]
    [StringLength(80)]
    public string Category { get; set; } = string.Empty;

    [StringLength(80)]
    public string? Subcategory { get; set; }

    [Required]
    public MemberDocumentSource Source { get; set; } = MemberDocumentSource.Uploaded;

    public DateTime? EffectiveDate { get; set; }
    public DateTime? ExpirationDate { get; set; }

    [Required]
    [StringLength(80)]
    public string RetentionPolicyId { get; set; } = "DEFAULT-10Y";

    public DateTime RetentionUntilDate { get; set; }

    public List<string> RelatedMemberIds { get; set; } = new();
    public List<string> LinkedResources { get; set; } = new();

    /// <summary>
    /// The file's id in the document catalog (document-service), where the
    /// bytes, retention, legal hold and access audit live. Duplicate content
    /// can share one catalog document between several member records.
    /// </summary>
    [StringLength(100)]
    public string? DocumentId { get; set; }

    [Required]
    [StringLength(120)]
    public string ContentType { get; set; } = "application/octet-stream";

    [Range(0, long.MaxValue)]
    public long SizeBytes { get; set; }

    [StringLength(64)]
    public string ContentHashSha256 { get; set; } = string.Empty;

    [StringLength(200)]
    public string UploadedBy { get; set; } = "system";

    public DateTime UploadedDate { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// While true the document cannot be deleted, modified or have its content
    /// replaced by anyone. Set and released only with records:legal-hold. The
    /// catalog holds the hold that is enforced; this record mirrors it.
    /// </summary>
    public bool LegalHold { get; set; }

    /// <summary>Who placed the current hold (token subject); null when not held.</summary>
    [StringLength(200)]
    public string? LegalHoldSetBy { get; set; }

    public DateTime? LegalHoldSetAt { get; set; }

    /// <summary>Why the current hold was placed, when a reason was given.</summary>
    [StringLength(LegalHoldRequest.MaxReasonLength)]
    public string? LegalHoldReason { get; set; }

    /// <summary>Every hold placed on and released from this document, oldest first.</summary>
    public List<LegalHoldEvent> LegalHoldHistory { get; set; } = new();

    [StringLength(2)]
    public string? StateCode { get; set; }

    public DateTime? CoverageTerminationDate { get; set; }

    /// <summary>
    /// True while a pre-signed upload has not been finalized: the content
    /// cannot be downloaded until finalize has validated it in the catalog.
    /// </summary>
    public bool UploadPending { get; set; }

    /// <summary>
    /// Incremented by every save. A save only replaces the version it read
    /// (a conditional replace on this field),
    /// so a legal-hold change and another write cannot silently overwrite each
    /// other: the later one gets <see cref="MemberDocumentConcurrencyException"/>
    /// (409). Documents saved before this field existed read as 0.
    /// </summary>
    public long Version { get; set; }
}

/// <summary>The document changed after it was read; the save was not applied.</summary>
public sealed class MemberDocumentConcurrencyException : Exception
{
    public MemberDocumentConcurrencyException(string documentId, Exception? inner = null)
        : base($"Member document {documentId} was changed by another request; reload it and retry.", inner)
    {
    }
}

public enum MemberDocumentSource
{
    Generated = 1,
    Uploaded = 2,
    Received = 3
}

public sealed class CreateMemberDocumentRequest
{
    [Required]
    public string MemberId { get; set; } = string.Empty;

    [Required]
    public string Category { get; set; } = string.Empty;

    public string? Subcategory { get; set; }
    public MemberDocumentSource Source { get; set; } = MemberDocumentSource.Uploaded;
    public DateTime? EffectiveDate { get; set; }
    public DateTime? ExpirationDate { get; set; }
    public string? RetentionPolicyId { get; set; }
    public List<string>? RelatedMemberIds { get; set; }
    public List<string>? LinkedResources { get; set; }
    // UploadedBy is ignored: the uploader is the token subject.
    public string? UploadedBy { get; set; }
    // Uploading under a legal hold places a hold: it needs records:legal-hold.
    public bool LegalHold { get; set; }
    [StringLength(LegalHoldRequest.MaxReasonLength)]
    public string? LegalHoldReason { get; set; }
    public string? StateCode { get; set; }
    public DateTime? CoverageTerminationDate { get; set; }
}

public sealed class PresignedUploadRequest
{
    [Required]
    public string MemberId { get; set; } = string.Empty;

    [Required]
    public string Category { get; set; } = string.Empty;

    [Required]
    public string FileName { get; set; } = string.Empty;

    public string ContentType { get; set; } = "application/octet-stream";
    public string? Subcategory { get; set; }
    // UploadedBy is ignored: the uploader is the token subject.
    public string? UploadedBy { get; set; }
    public string? RetentionPolicyId { get; set; }
    public string? StateCode { get; set; }
    public DateTime? CoverageTerminationDate { get; set; }
    // Uploading under a legal hold places a hold: it needs records:legal-hold.
    public bool LegalHold { get; set; }
    [StringLength(LegalHoldRequest.MaxReasonLength)]
    public string? LegalHoldReason { get; set; }
}

public sealed class PresignedUploadResponse
{
    public string DocumentId { get; set; } = string.Empty;
    public string UploadUrl { get; set; } = string.Empty;

    /// <summary>HTTP method for the upload (PUT).</summary>
    public string UploadMethod { get; set; } = "PUT";

    /// <summary>
    /// Headers the upload request must carry, exactly as given. They depend on
    /// the storage behind the link (Azure needs x-ms-blob-type), so clients copy
    /// them rather than assuming a provider.
    /// </summary>
    public Dictionary<string, string> UploadHeaders { get; set; } = new();
    public DateTime ExpiresAtUtc { get; set; }
}

/// <summary>
/// Places (<c>LegalHold = true</c>) or releases (<c>false</c>) a legal hold.
/// Requires records:legal-hold. A release must give a reason.
/// </summary>
public sealed class LegalHoldRequest
{
    public const int MaxReasonLength = 500;

    public bool LegalHold { get; set; }

    [StringLength(MaxReasonLength)]
    public string? Reason { get; set; }
}

public enum LegalHoldAction
{
    Set = 1,
    Released = 2
}

/// <summary>One placement or release of a legal hold, with who did it and why.</summary>
public sealed class LegalHoldEvent
{
    public LegalHoldAction Action { get; set; }

    [StringLength(200)]
    public string Actor { get; set; } = string.Empty;

    [StringLength(LegalHoldRequest.MaxReasonLength)]
    public string? Reason { get; set; }

    public DateTime At { get; set; }
}
