using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using MongoDB.Bson.Serialization.Attributes;

namespace ClaimsService.Models;

/// <summary>
/// Append-only audit record of a single AI Claims Examiner recommendation.
/// One row per AI run; the live "current" recommendation lives on
/// Claim.AiExamination, but every recommendation that has ever existed for
/// a claim is captured here so that:
///   1. Prompt-version A/B comparisons aren't confounded by overwrites.
///   2. The 90-day examiner-agreement analysis has a stable surface to query.
///   3. Re-runs (same claim, retried examiner pass) don't lose history.
///
/// Immutability rule:
///   Every field is immutable EXCEPT the three ExaminerAgreement* fields,
///   which are nullable until a human examiner acts on the claim and then
///   are written exactly once. The repository enforces single-write on those
///   fields; nothing else can be mutated post-create.
///
/// Cosmos partition key: TenantId.
/// </summary>
[BsonIgnoreExtraElements]
public class AiExaminationAudit
{
    /// <summary>Cosmos document id (also Mongo _id).</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Multi-tenant partition key.</summary>
    [Required]
    public string TenantId { get; set; } = string.Empty;

    /// <summary>The claim this recommendation was produced for.</summary>
    [Required]
    public string ClaimId { get; set; } = string.Empty;

    /// <summary>
    /// Pend code in effect at the moment the recommendation was generated
    /// (e.g., "NCCI"). Snapshotted so historical queries can correlate
    /// recommendation quality with the deterministic edit type that triggered
    /// it, even if the claim has since been re-pended for a different reason.
    /// </summary>
    [StringLength(20)]
    public string? PendCode { get; set; }

    /// <summary>
    /// Specific NCCI rule id (e.g., NE001) at recommendation time. Useful for
    /// per-rule override-rate analysis once we have enough data.
    /// </summary>
    [StringLength(10)]
    public string? RuleId { get; set; }

    /// <summary>
    /// Pair of CPT codes the model reasoned about, for NCCI bundling edits.
    /// Null for non-NCCI pend types (none in v1; reserved for phase 2).
    /// </summary>
    [StringLength(10)]
    public string? Column1Code { get; set; }

    [StringLength(10)]
    public string? Column2Code { get; set; }

    /// <summary>
    /// The recommendation as the model produced it. Field shape mirrors
    /// AiExamination so a single deserializer covers both audit and live.
    /// </summary>
    [Required]
    [StringLength(20)]
    public string RecommendedDisposition { get; set; } = "EscalateToHuman";

    [Range(0, 1)]
    public double ConfidenceScore { get; set; }

    [StringLength(4000)]
    public string? Rationale { get; set; }

    public List<string> PolicyCitations { get; set; } = new();

    /// <summary>
    /// Pinned model id used to produce this recommendation. Critical for
    /// attributing quality changes to model version vs. prompt version.
    /// </summary>
    [StringLength(100)]
    public string? ModelId { get; set; }

    /// <summary>
    /// Internal prompt template version (e.g., "ncci-pend-v1"). Lets the
    /// 90-day analysis bucket recommendations by exact prompt revision so
    /// A/B prompt rollouts don't pollute the override-rate signal.
    /// </summary>
    [StringLength(50)]
    public string? PromptVersion { get; set; }

    /// <summary>UTC timestamp the recommendation was generated.</summary>
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;

    // ─────────────────────────────────────────────────────────────────────
    // Examiner agreement — the only mutable section.
    // Set exactly once when a human examiner acts on the claim.
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Set when a human examiner acts. Values: Accepted | Modified | Overridden.
    /// Null while the recommendation is awaiting human review.
    /// </summary>
    [StringLength(20)]
    public string? ExaminerAgreement { get; set; }

    /// <summary>UTC timestamp when ExaminerAgreement was set.</summary>
    public DateTime? ExaminerActedAt { get; set; }

    /// <summary>Examiner who acted on the claim (set with ExaminerAgreement).</summary>
    [StringLength(200)]
    public string? ExaminerUserId { get; set; }

    /// <summary>Free-text note attached when the examiner overrode or modified.</summary>
    [StringLength(2000)]
    public string? ExaminerNotes { get; set; }
}
