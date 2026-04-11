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

    // ─────────────────────────────────────────────────────────────────────
    // AI Claims Examiner — auto-apply gates.
    //
    // Both flags default to FALSE. v1 of the AI Claims Examiner is strictly
    // pend-resolution: it produces an advisory recommendation that is always
    // routed to a human examiner via the work queue. Nothing auto-applies.
    //
    // These fields exist now so the v2 graduation path is a config flip,
    // not a schema migration. The auto-apply gating logic itself does NOT
    // exist anywhere in the codebase yet — when the 90-day examiner-agreement
    // analysis (see AiExaminationAudit collection) shows override rates near
    // zero for a specific edit type, that's when the orchestrator gains the
    // code path that reads these flags. Flipping the flag without that code
    // path is a no-op.
    //
    // Do NOT default either flag to true. The pend-resolution-first design
    // depends on humans staying in the loop until the override-rate evidence
    // says otherwise.
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// When true (and the v2 auto-apply code path exists), the AI examiner is
    /// permitted to auto-approve claims where its disposition is "Approve" and
    /// confidence ≥ AiExaminerConfidenceThreshold. Default: false.
    /// </summary>
    [JsonPropertyName("aiExaminerAutoPayEnabled")]
    public bool AiExaminerAutoPayEnabled { get; set; } = false;

    /// <summary>
    /// When true (and the v2 auto-apply code path exists), the AI examiner is
    /// permitted to auto-deny claims where its disposition is "Deny" and
    /// confidence ≥ AiExaminerConfidenceThreshold. Default: false. Should
    /// remain false longer than AutoPayEnabled — auto-denials carry higher
    /// regulatory exposure than auto-approvals.
    /// </summary>
    [JsonPropertyName("aiExaminerAutoDenyEnabled")]
    public bool AiExaminerAutoDenyEnabled { get; set; } = false;

    /// <summary>
    /// Minimum model confidence required for any future auto-apply path.
    /// Default 0.85 — tuned higher than the typical examiner-confidence band
    /// because the cost of an incorrect auto-apply is higher than the cost of
    /// a routine human review. Tenants can lower this on a per-edit-type basis
    /// once their override-rate data justifies it.
    /// </summary>
    [JsonPropertyName("aiExaminerConfidenceThreshold")]
    [Range(0, 1)]
    public double AiExaminerConfidenceThreshold { get; set; } = 0.85;

    /// <summary>
    /// Per-tenant kill switch for the AI Claims Examiner subscription. When
    /// false, claims-examiner-service skips this tenant's pend events entirely.
    /// Default true — tenants opt out, not in.
    /// </summary>
    [JsonPropertyName("aiExaminerEnabled")]
    public bool AiExaminerEnabled { get; set; } = true;

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
