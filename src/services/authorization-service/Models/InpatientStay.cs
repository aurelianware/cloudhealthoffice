using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using MongoDB.Bson.Serialization.Attributes;

namespace AuthorizationService.Models;

/// <summary>
/// An inpatient admission under utilization management.
///
/// A stay is not a second authorization. It is the thing concurrent review
/// reviews: the authorization says what was certified, the stay says how much
/// of it has been used, how far the certification currently reaches, and when
/// the next review falls due. Keeping the two apart is what lets a review
/// extend an existing certification (278 certification type <c>S</c>) instead
/// of opening a new authorization for every additional day.
/// </summary>
[BsonIgnoreExtraElements]
public class InpatientStay
{
    /// <summary>Owning tenant. Always set server-side from the caller's token.</summary>
    public string TenantId { get; set; } = string.Empty;

    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Optimistic-concurrency revision, bumped by every write. Two reviewers
    /// working the same census cannot silently overwrite each other.
    /// </summary>
    public long Revision { get; set; }

    /// <summary>
    /// The authorization this stay is certified under. Required: an
    /// uncertified admission is still a stay, but it is tracked against the
    /// authorization that covers it, even when that authorization is pended.
    /// </summary>
    [Required]
    [StringLength(50)]
    public string AuthorizationId { get; set; } = string.Empty;

    /// <summary>Denormalized for the census, which lists stays, not authorizations.</summary>
    [StringLength(50)]
    public string? AuthorizationNumber { get; set; }

    [Required]
    [StringLength(50)]
    public string MemberId { get; set; } = string.Empty;

    [StringLength(100)]
    public string? MemberFirstName { get; set; }

    [StringLength(100)]
    public string? MemberLastName { get; set; }

    [Required]
    public LineOfBusiness LineOfBusiness { get; set; }

    // ── Facility and attending ────────────────────────────────────

    [Required]
    [StringLength(10)]
    public string FacilityNPI { get; set; } = string.Empty;

    [StringLength(300)]
    public string? FacilityName { get; set; }

    [StringLength(10)]
    public string? AttendingProviderNPI { get; set; }

    [StringLength(300)]
    public string? AttendingProviderName { get; set; }

    // ── The admission ─────────────────────────────────────────────

    /// <summary>Admission date (UTC calendar date; the time of day is not clinically used here).</summary>
    [Required]
    public DateTime AdmissionDate { get; set; }

    /// <summary>UB-04 FL14 priority (type) of admission: 1 Emergency, 2 Urgent, 3 Elective, 4 Newborn, 5 Trauma.</summary>
    [StringLength(1)]
    public string? AdmissionType { get; set; }

    /// <summary>UB-04 FL15 point of origin.</summary>
    [StringLength(1)]
    public string? AdmissionSource { get; set; }

    /// <summary>Working principal diagnosis (ICD-10) at the time of review.</summary>
    [StringLength(10)]
    public string? PrincipalDiagnosisCode { get; set; }

    /// <summary>
    /// Level of care currently authorized, e.g. <c>inpatient-acute</c>,
    /// <c>observation</c>, <c>icu</c>, <c>snf</c>, <c>ltac</c>,
    /// <c>inpatient-rehab</c>. Free text: the vocabulary differs per criteria
    /// vendor and per state program, and nothing here needs to enumerate it.
    /// </summary>
    [StringLength(50)]
    public string? LevelOfCare { get; set; }

    // ── Certification state ───────────────────────────────────────

    /// <summary>
    /// Inclusive UTC date through which days are currently certified. Set by
    /// the initial certification and moved forward by each approving review.
    /// Null when nothing has been certified yet.
    /// </summary>
    public DateTime? CertifiedThroughDate { get; set; }

    /// <summary>Total days certified across every review, including the initial certification.</summary>
    public int CertifiedDays { get; set; }

    /// <summary>
    /// When the next concurrent review is due, as an inclusive UTC date. Null
    /// once the stay is discharged.
    /// </summary>
    public DateTime? NextReviewDueDate { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public InpatientStayStatus Status { get; set; } = InpatientStayStatus.Admitted;

    // ── Discharge ─────────────────────────────────────────────────

    public DateTime? DischargeDate { get; set; }

    /// <summary>UB-04 FL17 patient discharge status, e.g. 01 home, 03 SNF, 20 expired.</summary>
    [StringLength(2)]
    public string? DischargeDisposition { get; set; }

    /// <summary>
    /// Append-only concurrent-review history. Every certification decision on
    /// this stay is here, including the one that established it, so the
    /// certified-through date can always be re-derived from the reviews that
    /// produced it.
    /// </summary>
    public List<ConcurrentReviewRecord> Reviews { get; set; } = new();

    [StringLength(2000)]
    public string? Notes { get; set; }

    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
    public DateTime LastUpdatedDate { get; set; } = DateTime.UtcNow;

    [StringLength(200)]
    public string? CreatedBy { get; set; }

    [StringLength(200)]
    public string? LastUpdatedBy { get; set; }
}

/// <summary>One concurrent-review decision on a stay.</summary>
[BsonIgnoreExtraElements]
public class ConcurrentReviewRecord
{
    /// <summary>One-based review number within the stay. Review 1 is the initial certification.</summary>
    [Range(1, int.MaxValue)]
    public int ReviewNumber { get; set; }

    /// <summary>When the review was performed (UTC).</summary>
    public DateTime ReviewedAt { get; set; } = DateTime.UtcNow;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ConcurrentReviewOutcome Outcome { get; set; }

    /// <summary>
    /// Days certified by this review. Zero for a non-certifying outcome
    /// (denied, pended, discharge recorded).
    /// </summary>
    public int DaysCertified { get; set; }

    /// <summary>Inclusive UTC date this review certified through. Null when it certified nothing.</summary>
    public DateTime? CertifiedThroughDate { get; set; }

    /// <summary>Level of care this review continues or steps the member down to.</summary>
    [StringLength(50)]
    public string? LevelOfCare { get; set; }

    /// <summary>When the next review falls due after this one, as an inclusive UTC date.</summary>
    public DateTime? NextReviewDueDate { get; set; }

    // ── The criteria behind the decision ──────────────────────────

    /// <summary>
    /// Criteria provider that supported the decision ("interqual", "mcg",
    /// "cho-local"), or null when the reviewer decided without one.
    /// </summary>
    [StringLength(50)]
    public string? CriteriaProviderId { get; set; }

    /// <summary>Guideline applied, as the provider named it.</summary>
    [StringLength(100)]
    public string? CriteriaGuidelineId { get; set; }

    /// <summary>
    /// Content version or edition of that guideline. Recorded because a
    /// determination must be reconstructable after the vendor's content has
    /// moved on.
    /// </summary>
    [StringLength(100)]
    public string? CriteriaGuidelineVersion { get; set; }

    /// <summary>The criteria recommendation, as the provider returned it.</summary>
    [StringLength(30)]
    public string? CriteriaRecommendation { get; set; }

    /// <summary>The provider's own rationale wording, quoted in an adverse determination.</summary>
    public List<string> CriteriaRationale { get; set; } = new();

    // ── Who, and why ──────────────────────────────────────────────

    [StringLength(200)]
    public string? ReviewerId { get; set; }

    [StringLength(200)]
    public string? ReviewerName { get; set; }

    /// <summary>
    /// True when a physician reviewer signed this decision. Required for any
    /// adverse outcome: an adverse determination needs a reviewer of equal or
    /// greater clinical qualification.
    /// </summary>
    public bool PhysicianReviewed { get; set; }

    [StringLength(50)]
    public string? DenialReasonCode { get; set; }

    [StringLength(500)]
    public string? Reason { get; set; }
}

/// <summary>Where a stay sits.</summary>
public enum InpatientStayStatus
{
    /// <summary>The member is in the facility.</summary>
    Admitted = 1,

    /// <summary>The member has left. No further review is due.</summary>
    Discharged = 2,

    /// <summary>Opened in error. Kept for the audit trail, excluded from the census.</summary>
    Cancelled = 3
}

/// <summary>What a concurrent review decided.</summary>
public enum ConcurrentReviewOutcome
{
    /// <summary>Days certified as requested; the certification moves forward.</summary>
    Approved = 1,

    /// <summary>Fewer days certified than requested. Adverse in part, so it needs physician review.</summary>
    Modified = 2,

    /// <summary>No further days certified. Adverse, so it needs physician review.</summary>
    Denied = 3,

    /// <summary>
    /// Awaiting clinical information from the facility. Certifies nothing and
    /// does not move the certified-through date.
    /// </summary>
    Pended = 4
}
