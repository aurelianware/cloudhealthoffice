using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace AttachmentService.Models;

/// <summary>
/// Represents a 275 clinical attachment that can be linked to Claims, Authorizations, or Appeals
/// </summary>
public class Attachment
{
    /// <summary>
    /// Unique identifier for the attachment (Cosmos DB 'id')
    /// </summary>
    [Required]
    [StringLength(100)]
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Tenant identifier for multi-tenant isolation (partition key)
    /// </summary>
    [Required]
    [StringLength(50)]
    public string TenantId { get; set; } = string.Empty;

    /// <summary>
    /// Link to associated claim (from 837 transaction). Nullable, mutually exclusive with AuthorizationId/AppealId.
    /// </summary>
    [StringLength(100)]
    public string? ClaimId { get; set; }

    /// <summary>
    /// Link to associated authorization (from 278 transaction). Nullable, mutually exclusive with ClaimId/AppealId.
    /// </summary>
    [StringLength(100)]
    public string? AuthorizationId { get; set; }

    /// <summary>
    /// Link to associated appeal. Nullable, mutually exclusive with ClaimId/AuthorizationId.
    /// </summary>
    [StringLength(100)]
    public string? AppealId { get; set; }

    /// <summary>
    /// RFAI reference from 277 TRN02 segment. Present for solicited attachments, null for unsolicited.
    /// Links 278 Pended (A4) → 277 RFAI → 275 response.
    /// </summary>
    [StringLength(50)]
    public string? RFAIReference { get; set; }

    /// <summary>
    /// Attachment type: 'Solicited' (responding to RFAI) or 'Unsolicited' (proactive submission)
    /// </summary>
    [Required]
    [StringLength(20)]
    public string AttachmentType { get; set; } = "Unsolicited";

    /// <summary>
    /// Payer identifier (from 275 N1 segment)
    /// </summary>
    [Required]
    [StringLength(50)]
    public string PayerId { get; set; } = string.Empty;

    /// <summary>
    /// Payer name (from 275 N1 segment)
    /// </summary>
    [Required]
    [StringLength(100)]
    public string PayerName { get; set; } = string.Empty;

    /// <summary>
    /// Provider identifier (from 275 N1 segment)
    /// </summary>
    [Required]
    [StringLength(50)]
    public string ProviderId { get; set; } = string.Empty;

    /// <summary>
    /// Provider name (from 275 N1 segment)
    /// </summary>
    [StringLength(100)]
    public string? ProviderName { get; set; }

    /// <summary>
    /// Patient subscriber ID (from 275 SBR segment)
    /// </summary>
    [Required]
    [StringLength(50)]
    public string SubscriberId { get; set; } = string.Empty;

    /// <summary>
    /// Patient first name (from 275 NM1 segment)
    /// </summary>
    [StringLength(50)]
    public string? PatientFirstName { get; set; }

    /// <summary>
    /// Patient last name (from 275 NM1 segment)
    /// </summary>
    [StringLength(50)]
    public string? PatientLastName { get; set; }

    /// <summary>
    /// Document type (e.g., 'Medical Records', 'Lab Results', 'Imaging', 'Clinical Notes')
    /// From 275 PWK segment (Report Type Code)
    /// </summary>
    [Required]
    [StringLength(50)]
    public string DocumentType { get; set; } = string.Empty;

    /// <summary>
    /// Document format (e.g., 'PDF', 'JPEG', 'TIFF', 'XML')
    /// From 275 PWK segment (Report Transmission Code)
    /// </summary>
    [Required]
    [StringLength(20)]
    public string DocumentFormat { get; set; } = string.Empty;

    /// <summary>
    /// Azure Blob Storage URL for the attachment file
    /// </summary>
    [StringLength(500)]
    public string? BlobUrl { get; set; }

    /// <summary>
    /// Azure Blob Storage container name
    /// </summary>
    [StringLength(100)]
    public string? BlobContainerName { get; set; }

    /// <summary>
    /// Azure Blob Storage blob name (file path)
    /// </summary>
    [StringLength(200)]
    public string? BlobName { get; set; }

    /// <summary>
    /// File size in bytes
    /// </summary>
    public long? FileSizeBytes { get; set; }

    /// <summary>
    /// SHA-256 hash of file content for integrity verification
    /// </summary>
    [StringLength(64)]
    public string? FileHash { get; set; }

    /// <summary>
    /// X12 275 transaction raw EDI (for audit trail)
    /// </summary>
    public string? RawX12 { get; set; }

    /// <summary>
    /// Submission date/time (from 275 DTM segment or system time)
    /// </summary>
    [Required]
    public DateTime SubmittedDate { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Record creation timestamp
    /// </summary>
    [Required]
    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Processing status: 'Received', 'Validated', 'Linked', 'Failed'
    /// </summary>
    [Required]
    [StringLength(20)]
    public string Status { get; set; } = "Received";

    /// <summary>
    /// Additional notes or processing messages
    /// </summary>
    public string? Notes { get; set; }

    /// <summary>
    /// Structured rejection reason code when Status = "Failed".
    /// One of the constants defined in <see cref="AttachmentRejectionCode"/>.
    /// Drives the TED segment in the 824 Application Advice.
    /// </summary>
    [StringLength(30)]
    public string? RejectionCode { get; set; }

    /// <summary>
    /// Acknowledgment type sent: '999', '824', 'Both', or null if not yet sent
    /// </summary>
    [StringLength(10)]
    public string? AcknowledgmentType { get; set; }

    /// <summary>
    /// Whether acknowledgment has been sent to provider
    /// </summary>
    public bool AcknowledgmentSent { get; set; } = false;

    /// <summary>
    /// Date/time acknowledgment was sent
    /// </summary>
    public DateTime? AcknowledgmentSentDate { get; set; }

    /// <summary>
    /// Generated 999 Implementation Acknowledgment (raw EDI)
    /// </summary>
    public string? Generated999 { get; set; }

    /// <summary>
    /// Generated 824 Application Advice (raw EDI)
    /// </summary>
    public string? Generated824 { get; set; }
}
