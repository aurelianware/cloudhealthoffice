using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace AttachmentService.Models;

/// <summary>
/// Trading partner configuration for EDI acknowledgment preferences
/// </summary>
public class TradingPartner
{
    /// <summary>
    /// Unique identifier for the trading partner (Cosmos DB 'id')
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
    /// Trading partner identifier (e.g., payer ID)
    /// </summary>
    [Required]
    [StringLength(50)]
    public string PartnerId { get; set; } = string.Empty;

    /// <summary>
    /// Trading partner name
    /// </summary>
    [Required]
    [StringLength(100)]
    public string PartnerName { get; set; } = string.Empty;

    /// <summary>
    /// Preferred acknowledgment type for 275 attachments: '999', '824', or 'Both'
    /// </summary>
    [Required]
    [StringLength(10)]
    public string AttachmentAckType { get; set; } = "999";

    /// <summary>
    /// Preferred acknowledgment type for 837 claims: '999', '277', or 'Both'
    /// </summary>
    [StringLength(10)]
    public string ClaimAckType { get; set; } = "999";

    /// <summary>
    /// Whether to send acknowledgments automatically
    /// </summary>
    public bool AutoSendAcknowledgments { get; set; } = true;

    /// <summary>
    /// Interchange sender ID (ISA06)
    /// </summary>
    [StringLength(15)]
    public string? InterchangeSenderId { get; set; }

    /// <summary>
    /// Interchange receiver ID (ISA08)
    /// </summary>
    [StringLength(15)]
    public string? InterchangeReceiverId { get; set; }

    /// <summary>
    /// Application sender code (GS02)
    /// </summary>
    [StringLength(15)]
    public string? ApplicationSenderId { get; set; }

    /// <summary>
    /// Application receiver code (GS03)
    /// </summary>
    [StringLength(15)]
    public string? ApplicationReceiverId { get; set; }

    /// <summary>
    /// Record creation timestamp
    /// </summary>
    [Required]
    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Last update timestamp
    /// </summary>
    public DateTime? UpdatedDate { get; set; }

    /// <summary>
    /// Whether this trading partner is active
    /// </summary>
    public bool IsActive { get; set; } = true;
}
