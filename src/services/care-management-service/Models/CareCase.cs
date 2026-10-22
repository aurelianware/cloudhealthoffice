using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using MongoDB.Bson.Serialization.Attributes;

namespace CareManagementService.Models;

/// <summary>
/// A care management case: one member, one program, one care manager, for as
/// long as the member is enrolled in it.
///
/// A case is not an authorization and not a claim. Utilization management asks
/// whether a service should be covered; care management asks what happens to
/// the member between services. The two meet at the edges — a discharge or a
/// denial can open a case — but the case outlives any one of them.
/// </summary>
[BsonIgnoreExtraElements]
public class CareCase
{
    /// <summary>Owning tenant. Always set server-side from the caller's token.</summary>
    public string TenantId { get; set; } = string.Empty;

    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Optimistic-concurrency revision, bumped by every write. A care manager
    /// and a nurse working the same case cannot silently overwrite each other.
    /// </summary>
    public long Revision { get; set; }

    /// <summary>Human-facing case number, unique within the tenant.</summary>
    [Required]
    [StringLength(50)]
    public string CaseNumber { get; set; } = string.Empty;

    [Required]
    [StringLength(50)]
    public string MemberId { get; set; } = string.Empty;

    [StringLength(100)]
    public string? MemberFirstName { get; set; }

    [StringLength(100)]
    public string? MemberLastName { get; set; }

    [Required]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public CareProgram Program { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public CareCaseStatus Status { get; set; } = CareCaseStatus.Referred;

    /// <summary>
    /// How much attention the case needs. Set at screening and revisited;
    /// drives the work queue, not eligibility.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public CareAcuity Acuity { get; set; } = CareAcuity.Unknown;

    // ── Who owns it ───────────────────────────────────────────────

    /// <summary>Care manager who owns the case. Null while it is unassigned.</summary>
    [StringLength(200)]
    public string? CareManagerId { get; set; }

    [StringLength(200)]
    public string? CareManagerName { get; set; }

    /// <summary>
    /// Everyone involved in the member's care, including people outside the
    /// plan. Projected as a FHIR CareTeam.
    /// </summary>
    public List<CareTeamMember> CareTeam { get; set; } = new();

    // ── Where it came from ────────────────────────────────────────

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public CareReferralSource ReferralSource { get; set; } = CareReferralSource.Unknown;

    /// <summary>
    /// What referred the case, where something did: an authorization id, an
    /// inpatient stay id, a claim number. Free text because the referring
    /// systems differ.
    /// </summary>
    [StringLength(100)]
    public string? ReferralReference { get; set; }

    [StringLength(500)]
    public string? ReferralReason { get; set; }

    // ── Consent ───────────────────────────────────────────────────

    /// <summary>
    /// Whether the member has agreed to take part. Care management is
    /// voluntary: a case can be opened and worked toward outreach without it,
    /// but a care plan cannot be activated until the member consents.
    /// </summary>
    public bool MemberConsentObtained { get; set; }

    public DateTime? MemberConsentDate { get; set; }

    /// <summary>Set when the member declined, so the case is not reopened by the same trigger.</summary>
    [StringLength(500)]
    public string? DeclineReason { get; set; }

    // ── Dates ─────────────────────────────────────────────────────

    public DateTime ReferredDate { get; set; } = DateTime.UtcNow;
    public DateTime? OpenedDate { get; set; }
    public DateTime? ClosedDate { get; set; }

    [StringLength(500)]
    public string? ClosureReason { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public CareCaseClosureOutcome? ClosureOutcome { get; set; }

    /// <summary>When the care manager should next touch the case.</summary>
    public DateTime? NextContactDueDate { get; set; }

    // ── The work itself ───────────────────────────────────────────

    public List<CareAssessment> Assessments { get; set; } = new();

    /// <summary>
    /// The member's care plan. Null until one is drafted; a case can be worked
    /// (outreach, screening) before there is a plan.
    /// </summary>
    public CarePlan? CarePlan { get; set; }

    public List<CareActivity> Activities { get; set; } = new();

    /// <summary>Append-only status history, so how the case got here survives.</summary>
    public List<CareCaseStatusChange> StatusHistory { get; set; } = new();

    [StringLength(4000)]
    public string? Notes { get; set; }

    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
    public DateTime LastUpdatedDate { get; set; } = DateTime.UtcNow;

    [StringLength(200)]
    public string? CreatedBy { get; set; }

    [StringLength(200)]
    public string? LastUpdatedBy { get; set; }
}

/// <summary>Someone involved in the member's care.</summary>
[BsonIgnoreExtraElements]
public class CareTeamMember
{
    [Required]
    [StringLength(200)]
    public string Name { get; set; } = string.Empty;

    /// <summary>Their part in the member's care: care manager, PCP, specialist, caregiver, pharmacist.</summary>
    [Required]
    [StringLength(50)]
    public string Role { get; set; } = string.Empty;

    [StringLength(10)]
    public string? Npi { get; set; }

    [StringLength(100)]
    public string? Organization { get; set; }

    [StringLength(50)]
    public string? Phone { get; set; }

    /// <summary>True for someone outside the plan, such as a family caregiver.</summary>
    public bool External { get; set; }
}

/// <summary>One entry in a case's append-only status history.</summary>
[BsonIgnoreExtraElements]
public class CareCaseStatusChange
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public CareCaseStatus Status { get; set; }

    [StringLength(500)]
    public string? Reason { get; set; }

    public DateTime ChangedAt { get; set; } = DateTime.UtcNow;

    [StringLength(200)]
    public string? ChangedBy { get; set; }
}

/// <summary>Which care management program the case belongs to.</summary>
public enum CareProgram
{
    Unknown = 0,

    /// <summary>Complex case management: high-acuity members with multiple conditions.</summary>
    ComplexCaseManagement = 1,

    /// <summary>Disease management for a single chronic condition.</summary>
    DiseaseManagement = 2,

    /// <summary>Transitions of care: the window around a discharge.</summary>
    TransitionsOfCare = 3,

    /// <summary>Maternity and high-risk pregnancy management.</summary>
    MaternityManagement = 4,

    /// <summary>Behavioral health care management.</summary>
    BehavioralHealth = 5,

    /// <summary>Long-term services and supports.</summary>
    LongTermServicesAndSupports = 6
}

/// <summary>Where a case sits.</summary>
public enum CareCaseStatus
{
    /// <summary>Referred and waiting to be screened. Nobody owns it yet.</summary>
    Referred = 1,

    /// <summary>Being screened for eligibility and acuity.</summary>
    Screening = 2,

    /// <summary>The plan is trying to reach the member.</summary>
    Outreach = 3,

    /// <summary>The member is enrolled and being worked.</summary>
    Active = 4,

    /// <summary>Open but paused — the member is unreachable or has asked for a pause.</summary>
    OnHold = 5,

    /// <summary>Closed. Terminal; reopening starts a new case.</summary>
    Closed = 6,

    /// <summary>The member declined to take part. Terminal.</summary>
    Declined = 7
}

/// <summary>How much attention the case needs.</summary>
public enum CareAcuity
{
    Unknown = 0,
    Low = 1,
    Moderate = 2,
    High = 3,

    /// <summary>Needs contact within a day.</summary>
    Critical = 4
}

/// <summary>What referred the case.</summary>
public enum CareReferralSource
{
    Unknown = 0,

    /// <summary>A discharge recorded by concurrent review.</summary>
    InpatientDischarge = 1,

    /// <summary>A prior authorization — commonly a denial or a high-acuity request.</summary>
    PriorAuthorization = 2,

    /// <summary>A predictive or risk model.</summary>
    RiskStratification = 3,

    /// <summary>A provider asked for it.</summary>
    ProviderReferral = 4,

    /// <summary>The member asked for it.</summary>
    MemberSelfReferral = 5,

    /// <summary>Claims review — utilization patterns, gaps in care.</summary>
    ClaimsReview = 6,

    /// <summary>Emergency department utilization.</summary>
    EmergencyDepartmentUtilization = 7
}

/// <summary>How a case ended. Separate from the reason, which is free text.</summary>
public enum CareCaseClosureOutcome
{
    /// <summary>The care plan's goals were met.</summary>
    GoalsMet = 1,

    /// <summary>Some progress, but the case closed before the goals were met.</summary>
    PartiallyMet = 2,

    /// <summary>The member could not be reached after the outreach attempts the program requires.</summary>
    UnableToContact = 3,

    /// <summary>The member asked to stop.</summary>
    MemberWithdrew = 4,

    /// <summary>The member left the plan.</summary>
    NoLongerEligible = 5,

    /// <summary>Moved to another program or another care manager's case.</summary>
    TransferredToAnotherProgram = 6,

    /// <summary>The member died.</summary>
    Deceased = 7
}
