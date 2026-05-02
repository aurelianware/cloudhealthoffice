using System.ComponentModel.DataAnnotations;

namespace ClaimsService.Models;

/// <summary>
/// Operator-supplied request body for
/// <c>POST /api/v1/claims/{predecessorClaimId}/adjustments</c>
/// (capability 5.12). Carries the corrected claim payload (same
/// vendor-neutral <see cref="AdapterClaim"/> shape as the canonical 5.3
/// submission surface) plus operator metadata for the audit chain.
///
/// <para>
/// The <c>Idempotency-Key</c> request header (per Decision 6) is the
/// dedup signal — same key + same body returns the existing adjustment
/// with 200; same key + different body returns 409. The body itself does
/// not carry the key — it lives in the header so callers can retry the
/// same payload deterministically.
/// </para>
/// </summary>
public class ClaimAdjustmentRequest
{
    /// <summary>Operator-supplied reason. Required for the audit trail.</summary>
    [Required]
    [StringLength(500, MinimumLength = 1)]
    public string AdjustmentReason { get; set; } = string.Empty;

    /// <summary>Optional free-text notes; capped at 2000 characters.</summary>
    [StringLength(2000)]
    public string? Notes { get; set; }

    /// <summary>
    /// Corrected claim payload — same shape as 5.3 submission. The
    /// adjustment service overrides identity-bearing fields
    /// (<c>Id</c>, <c>ClaimVersionId</c>, <c>VersionNumber</c>,
    /// <c>VersionState</c>, <c>PredecessorVersionId</c>,
    /// <c>SubmittedDate</c>) so callers can supply the corrected
    /// content of the prior version without worrying about chain
    /// bookkeeping. Per Decision 6 this is full-payload (not
    /// partial-edit); per Decision 4 the new version runs the full
    /// 6-stage pipeline unchanged.
    /// </summary>
    [Required]
    public AdapterClaim CorrectedClaim { get; set; } = new();
}

/// <summary>
/// Query string filters for
/// <c>GET /api/v1/adjustments?status=...&amp;...</c> (Gap 3 ratification).
/// Mirrors PaymentRunCriteria filter shape so 5.12b's ReversalRunService
/// has a single query path for batch creation.
/// </summary>
public class ClaimAdjustmentListFilter
{
    public ClaimAdjustmentStatus? Status { get; set; }
    public string? PredecessorClaimId { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? CreatedFrom { get; set; }
    public DateTime? CreatedTo { get; set; }

    [Range(1, int.MaxValue)]
    public int Page { get; set; } = 1;

    [Range(1, 200)]
    public int PageSize { get; set; } = 50;
}
