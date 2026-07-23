using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using MongoDB.Bson.Serialization.Attributes;

namespace MemberService.Models;

/// <summary>
/// Represents a health plan member (subscriber or dependent).
/// Populated by X12 834 Enrollment transactions (INS/NM1/DMG segments).
/// </summary>
[BsonIgnoreExtraElements]
public class Member
{
    /// <summary>
    /// Multi-tenant partition key (required for Cosmos DB isolation)
    /// </summary>
    [Required]
    public string TenantId { get; set; } = string.Empty;

    /// <summary>
    /// Unique identifier (Cosmos DB document id)
    /// </summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Member ID from 834 REF*0F segment or generated
    /// </summary>
    [Required]
    [StringLength(50)]
    public string MemberId { get; set; } = string.Empty;

    /// <summary>
    /// Social Security Number from 834 REF*SY segment (encrypted/masked in production)
    /// </summary>
    [StringLength(11)]
    public string? SSN { get; set; }

    /// <summary>
    /// Group number linking to Sponsor Service (834 REF*1L)
    /// </summary>
    [Required]
    [StringLength(50)]
    public string GroupNumber { get; set; } = string.Empty;

    /// <summary>
    /// Is this member a subscriber (true) or dependent (false) - 834 INS01 segment
    /// INS*Y = Subscriber, INS*N = Dependent
    /// </summary>
    [Required]
    public bool IsSubscriber { get; set; }

    /// <summary>
    /// For dependents: link to subscriber's MemberId.
    /// </summary>
    /// <remarks>
    /// Legacy FK, retained for back-compat with callers that read the Member directly.
    /// On read, <see cref="MembersController"/> overwrites this from the active
    /// <c>FamilyRelationship</c> graph (see docs/migrations/family-relationships-backfill.md).
    /// Slated for removal in a future major version — new write paths should create a
    /// <c>FamilyRelationship</c> instead of setting this field.
    /// </remarks>
    [Obsolete("Derived from FamilyRelationship graph on read. New writes must go through FamilyRelationshipService. See docs/migrations/family-relationships-backfill.md.", error: false)]
    [StringLength(50)]
    public string? SubscriberMemberId { get; set; }

    /// <summary>
    /// Relationship to subscriber from 834 INS02 segment
    /// 18 = Self, 01 = Spouse, 19 = Child, 53 = Life Partner, etc.
    /// </summary>
    [StringLength(2)]
    public string? RelationshipCode { get; set; }

    /// <summary>
    /// First name from 834 NM104 segment
    /// </summary>
    [Required]
    [StringLength(100)]
    public string FirstName { get; set; } = string.Empty;

    /// <summary>
    /// Last name from 834 NM103 segment
    /// </summary>
    [Required]
    [StringLength(100)]
    public string LastName { get; set; } = string.Empty;

    /// <summary>
    /// Middle name from 834 NM105 segment
    /// </summary>
    [StringLength(100)]
    public string? MiddleName { get; set; }

    /// <summary>
    /// Date of birth from 834 DMG02 segment (format: CCYYMMDD)
    /// </summary>
    [Required]
    public DateTime DateOfBirth { get; set; }

    /// <summary>
    /// Gender from 834 DMG03 segment (M = Male, F = Female, U = Unknown)
    /// </summary>
    [StringLength(1)]
    public string? Gender { get; set; }

    /// <summary>
    /// Street address from 834 N3 segment
    /// </summary>
    [StringLength(300)]
    public string? Address { get; set; }

    /// <summary>
    /// City from 834 N4 segment
    /// </summary>
    [StringLength(100)]
    public string? City { get; set; }

    /// <summary>
    /// State code from 834 N4 segment (e.g., "TX", "CA")
    /// </summary>
    [StringLength(2)]
    public string? State { get; set; }

    /// <summary>
    /// ZIP code from 834 N4 segment
    /// </summary>
    [StringLength(10)]
    public string? ZipCode { get; set; }

    /// <summary>
    /// Phone number from 834 PER segment
    /// </summary>
    [Phone]
    [StringLength(20)]
    public string? Phone { get; set; }

    /// <summary>
    /// Email address from 834 PER segment
    /// </summary>
    [EmailAddress]
    [StringLength(200)]
    public string? Email { get; set; }

    /// <summary>
    /// Coverage effective date from 834 DTP*348 segment
    /// </summary>
    [Required]
    public DateTime EffectiveDate { get; set; }

    /// <summary>
    /// Coverage termination date from 834 DTP*349 segment
    /// </summary>
    public DateTime? TerminationDate { get; set; }

    /// <summary>
    /// Enrollment status
    /// </summary>
    [Required]
    public EnrollmentStatus Status { get; set; } = EnrollmentStatus.Active;

    /// <summary>
    /// Line of Business (Commercial, Medicare, Medicaid, Exchange)
    /// Inherited from sponsor/plan selection
    /// </summary>
    [Required]
    public LineOfBusiness LineOfBusiness { get; set; } = LineOfBusiness.Commercial;

    /// <summary>
    /// Maintenance type code from 834 INS03 segment
    /// 001 = Change, 021 = Addition, 024 = Cancellation or Termination, 030 = Audit or Compare
    /// </summary>
    [StringLength(3)]
    public string? MaintenanceTypeCode { get; set; }

    /// <summary>
    /// Maintenance reason code from 834 INS04 segment
    /// 25 = Change in Identifying Data, 32 = Divorce, 33 = Birth, etc.
    /// </summary>
    [StringLength(3)]
    public string? MaintenanceReasonCode { get; set; }

    /// <summary>
    /// Retroactive effective date of a benefit-plan/coverage change
    /// (<see cref="MaintenanceTypeCode"/> 001) recorded after the fact. Null
    /// unless this member record reflects a correction whose effective date
    /// precedes when it was processed; claims with a service date on or
    /// after this date were adjudicated before the correct plan assignment
    /// was known and require reconciliation.
    /// </summary>
    public DateTime? PlanChangeEffectiveDate { get; set; }

    /// <summary>
    /// Medicaid "medically needy" spend-down liability for the member's
    /// current budget period: the dollar amount of incurred medical
    /// expenses the member must accumulate before Medicaid coverage
    /// activates for that period. Null for members not enrolled under a
    /// spend-down eligibility category.
    /// </summary>
    public decimal? MedicaidSpendDownLiabilityAmount { get; set; }

    /// <summary>
    /// Cumulative amount the member has incurred toward
    /// <see cref="MedicaidSpendDownLiabilityAmount"/> in the current budget
    /// period. Meaningless when the liability amount is null.
    /// </summary>
    public decimal MedicaidSpendDownAmountMet { get; set; }

    /// <summary>
    /// Employment status from 834 EMP segment
    /// </summary>
    public EmploymentStatus? EmploymentStatus { get; set; }

    /// <summary>
    /// Tobacco use indicator (affects premium calculations)
    /// </summary>
    public bool? TobaccoUser { get; set; }

    /// <summary>
    /// Student status (for dependent age 19-26)
    /// </summary>
    public bool? IsStudent { get; set; }

    /// <summary>
    /// Audit: Record creation timestamp
    /// </summary>
    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Audit: Last modification timestamp
    /// </summary>
    public DateTime LastUpdatedDate { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Audit: Created by user/system
    /// </summary>
    [StringLength(200)]
    public string? CreatedBy { get; set; }

    /// <summary>
    /// Audit: Last updated by user/system
    /// </summary>
    [StringLength(200)]
    public string? LastUpdatedBy { get; set; }

    /// <summary>
    /// Full name helper property
    /// </summary>
    public string FullName => $"{FirstName} {MiddleName} {LastName}".Replace("  ", " ").Trim();

    // ── FHIR Patient projection fields (US Core) ─────────────────────

    /// <summary>
    /// Typed identifiers (Medicaid, MBI, Exchange, Portal, Legacy, etc.).
    /// PII identifiers (SSN/MBI/Medicaid) should be stored encrypted-at-rest.
    /// </summary>
    public List<MemberIdentifier> Identifiers { get; set; } = new();

    /// <summary>
    /// BCP-47 preferred language (e.g. "en-US"). Projects to FHIR Patient.communication.preferred=true.
    /// </summary>
    [StringLength(16)]
    public string? PreferredLanguage { get; set; }

    /// <summary>
    /// Additional BCP-47 languages the member speaks.
    /// </summary>
    public List<string> Languages { get; set; } = new();

    /// <summary>
    /// OMB race category (US Core us-core-race extension, ombCategory).
    /// System: urn:oid:2.16.840.1.113883.6.238
    /// </summary>
    public CodedConcept? Race { get; set; }

    /// <summary>
    /// Detailed race codes beyond the five OMB buckets.
    /// </summary>
    public List<CodedConcept> RaceDetail { get; set; } = new();

    /// <summary>
    /// OMB ethnicity (us-core-ethnicity, ombCategory).
    /// </summary>
    public CodedConcept? Ethnicity { get; set; }

    public List<CodedConcept> EthnicityDetail { get; set; } = new();

    /// <summary>
    /// Self-reported gender identity (us-core-genderIdentity extension).
    /// </summary>
    public CodedConcept? GenderIdentity { get; set; }

    [StringLength(100)]
    public string? Pronouns { get; set; }

    /// <summary>
    /// MaritalStatus (HL7 v3 MaritalStatus code system).
    /// </summary>
    public CodedConcept? MaritalStatus { get; set; }

    /// <summary>
    /// Deceased indicator (FHIR Patient.deceasedBoolean). True when the member is deceased.
    /// </summary>
    public bool Deceased { get; set; }

    /// <summary>
    /// Date of death (FHIR Patient.deceasedDateTime) when known.
    /// </summary>
    public DateTime? DeceasedDate { get; set; }

    /// <summary>
    /// Sex assigned at birth (us-core-birthsex extension). M | F | UNK.
    /// </summary>
    [StringLength(3)]
    public string? BirthSex { get; set; }

    /// <summary>
    /// Communication channel preferences (opt-in, windows, per-channel language override).
    /// </summary>
    public List<CommunicationPreference> CommunicationPreferences { get; set; } = new();

    /// <summary>
    /// True while the Member is in a partially-constructed state (e.g., Add-Dependent
    /// wizard has created the Member but not yet created its <c>FamilyRelationship</c>).
    /// Drafts are hidden from standard search/read paths. A background reconciler
    /// promotes or purges drafts after a TTL. See <c>FamilyRelationshipsController.AddDependent</c>.
    /// </summary>
    public bool IsDraft { get; set; }
}

/// <summary>
/// Member enrollment status
/// </summary>
public enum EnrollmentStatus
{
    /// <summary>
    /// Member is actively enrolled with coverage
    /// </summary>
    Active = 1,

    /// <summary>
    /// Member enrollment is pending (not yet effective)
    /// </summary>
    Pending = 2,

    /// <summary>
    /// Member coverage is terminated
    /// </summary>
    Terminated = 3,

    /// <summary>
    /// Member enrollment is suspended (e.g., COBRA grace period)
    /// </summary>
    Suspended = 4,

    /// <summary>
    /// COBRA continuation coverage
    /// </summary>
    COBRA = 5
}

/// <summary>
/// Employment status (affects eligibility)
/// </summary>
public enum EmploymentStatus
{
    /// <summary>
    /// Active full-time employee
    /// </summary>
    FullTime = 1,

    /// <summary>
    /// Active part-time employee
    /// </summary>
    PartTime = 2,

    /// <summary>
    /// Retired
    /// </summary>
    Retired = 3,

    /// <summary>
    /// Leave of absence
    /// </summary>
    LeaveOfAbsence = 4,

    /// <summary>
    /// Terminated employment
    /// </summary>
    Terminated = 5,

    /// <summary>
    /// Not employed (dependent)
    /// </summary>
    NotEmployed = 6
}

/// <summary>
/// Common X12 834 relationship codes
/// </summary>
public static class RelationshipCodes
{
    public const string Self = "18";
    public const string Spouse = "01";
    public const string Child = "19";
    public const string Employee = "18";
    public const string LifePartner = "53";
    public const string Stepchild = "17";
    public const string FosterChild = "10";
    public const string DomesticPartner = "53";
    public const string Other = "G8";
}

/// <summary>
/// Line of Business - determines regulatory requirements and benefit rules
/// </summary>
public enum LineOfBusiness
{
    Commercial = 1,
    Medicare = 2,
    Medicaid = 3,
    Exchange = 4,
    TRICARE = 5,
    VA = 6
}
