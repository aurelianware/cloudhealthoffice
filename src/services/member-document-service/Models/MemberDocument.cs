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

    [Required]
    [StringLength(100)]
    public string BlobContainer { get; set; } = "member-documents";

    [Required]
    [StringLength(500)]
    public string BlobPath { get; set; } = string.Empty;

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

    public bool LegalHold { get; set; }

    [StringLength(2)]
    public string? StateCode { get; set; }

    public DateTime? CoverageTerminationDate { get; set; }
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
    public string? UploadedBy { get; set; }
    public bool LegalHold { get; set; }
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
    public string? UploadedBy { get; set; }
    public string? RetentionPolicyId { get; set; }
    public string? StateCode { get; set; }
    public DateTime? CoverageTerminationDate { get; set; }
    public bool LegalHold { get; set; }
}

public sealed class PresignedUploadResponse
{
    public string DocumentId { get; set; } = string.Empty;
    public string UploadUrl { get; set; } = string.Empty;
    public string BlobPath { get; set; } = string.Empty;
    public DateTime ExpiresAtUtc { get; set; }
}

public sealed class LegalHoldRequest
{
    public bool LegalHold { get; set; }
}
