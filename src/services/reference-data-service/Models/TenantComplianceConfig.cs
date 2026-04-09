using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace ReferenceDataService.Models;

/// <summary>
/// Cosmos DB document that stores state-specific compliance parameters for a tenant.
/// Partition key: <see cref="TenantId"/>.
/// Read at runtime by claims, authorization, appeals, encounter, and payment services
/// to enforce regulatory deadlines (prompt pay, PA turnaround, etc.).
/// </summary>
public class TenantComplianceConfig
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
    /// Two-letter state code identifying the regulatory jurisdiction (e.g. "FL").
    /// </summary>
    [JsonPropertyName("stateCode")]
    [Required]
    [StringLength(2)]
    public string StateCode { get; set; } = string.Empty;

    /// <summary>
    /// Embedded state compliance parameters (prompt pay deadlines, PA timelines, etc.).
    /// </summary>
    [JsonPropertyName("stateConfig")]
    public StateComplianceConfig StateConfig { get; set; } = new();

    /// <summary>
    /// ISA06 Submitter ID used when transmitting X12 batch files to Florida FMMIS.
    /// </summary>
    [JsonPropertyName("fmmisSubmitterId")]
    [StringLength(15)]
    public string FmmisSubmitterId { get; set; } = string.Empty;

    /// <summary>
    /// ISA08 Interchange Sender ID used in the ISA header for FL FMMIS transmissions.
    /// </summary>
    [JsonPropertyName("fmmisInterchangeSenderId")]
    [StringLength(15)]
    public string FmmisInterchangeSenderId { get; set; } = string.Empty;

    /// <summary>
    /// Indicates whether the tenant participates in the SMMC 3.0
    /// Managed Medical Assistance Program Improvement Project (MPIP).
    /// </summary>
    [JsonPropertyName("mpipEnabled")]
    public bool MpipEnabled { get; set; }

    /// <summary>
    /// Timestamp when this configuration document was created.
    /// </summary>
    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Timestamp when this configuration document was last updated.
    /// </summary>
    [JsonPropertyName("updatedAt")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
