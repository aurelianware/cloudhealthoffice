using System.Text.Json.Serialization;

namespace EnrollmentImportService.Models;

/// <summary>
/// Parsed 834 enrollment transaction
/// </summary>
public class Enrollment834
{
    [JsonPropertyName("fileName")]
    public string FileName { get; set; } = string.Empty;

    [JsonPropertyName("parsedAt")]
    public DateTime ParsedAt { get; set; }

    [JsonPropertyName("transactionCount")]
    public int TransactionCount { get; set; }

    /// <summary>
    /// One entry per subscriber Loop 2000 (INS01=Y). Dependent Loop 2000s
    /// (INS01=N) in the same transaction set are attached to the subscriber
    /// whose REF*0F matches, under <see cref="MemberEnrollment.Dependents"/>.
    /// </summary>
    [JsonPropertyName("enrollments")]
    public List<MemberEnrollment> Enrollments { get; set; } = new();

    /// <summary>
    /// Dependent Loop 2000s whose subscriber's own INS loop is not in the
    /// same transaction set — e.g. a newborn added, or a child terminated,
    /// without resending the subscriber. Each is linked to an existing
    /// subscriber via <see cref="Dependent.SubscriberId"/> (REF*0F).
    /// </summary>
    [JsonPropertyName("dependentEnrollments")]
    public List<Dependent> DependentEnrollments { get; set; } = new();

    /// <summary>
    /// Optional caller-supplied batch id. When set, replays of the same batch produce
    /// deterministic event ids and de-duplicate at the event store.
    /// </summary>
    [JsonPropertyName("batchId")]
    public string? BatchId { get; set; }

    /// <summary>True when this batch came from a manual entry endpoint, not an 834 file.</summary>
    [JsonPropertyName("manualSource")]
    public bool ManualSource { get; set; }

    /// <summary>
    /// The user (token subject) running this import. Set by the controller from the
    /// validated token; [JsonIgnore] so a request body can never supply it.
    /// </summary>
    [JsonIgnore]
    public string? ActorId { get; set; }
}

public class MemberEnrollment
{
    [JsonPropertyName("relationship")]
    public string Relationship { get; set; } = string.Empty; // INS02: 18=Self, 01=Spouse, 19=Child
    
    [JsonPropertyName("maintenanceType")]
    public string MaintenanceType { get; set; } = string.Empty; // 001=Change, 021=Addition, 024=Termination
    
    [JsonPropertyName("maintenanceReason")]
    public string? MaintenanceReason { get; set; }
    
    [JsonPropertyName("benefitStatus")]
    public string BenefitStatus { get; set; } = string.Empty; // A=Active, C=COBRA, T=Terminated
    
    [JsonPropertyName("subscriberId")]
    public string? SubscriberId { get; set; }
    
    [JsonPropertyName("groupNumber")]
    public string? GroupNumber { get; set; }
    
    [JsonPropertyName("employeeId")]
    public string? EmployeeId { get; set; }
    
    [JsonPropertyName("enrollmentDate")]
    public string? EnrollmentDate { get; set; }
    
    /// <summary>
    /// Member eligibility end (Loop 2000 DTP*357). For a 024 termination
    /// that carries no DTP*357, the 834 parser falls back to the latest
    /// coverage-level benefit end date (Loop 2300 DTP*349).
    /// </summary>
    [JsonPropertyName("terminationDate")]
    public string? TerminationDate { get; set; }

    /// <summary>Member eligibility begin (Loop 2000 DTP*356).</summary>
    [JsonPropertyName("eligibilityBeginDate")]
    public string? EligibilityBeginDate { get; set; }

    [JsonPropertyName("employmentStartDate")]
    public string? EmploymentStartDate { get; set; }
    
    [JsonPropertyName("demographics")]
    public Demographics? Demographics { get; set; }
    
    [JsonPropertyName("sponsor")]
    public Sponsor? Sponsor { get; set; }
    
    [JsonPropertyName("coverage")]
    public List<CoverageDetail> Coverage { get; set; } = new();
    
    [JsonPropertyName("dependents")]
    public List<Dependent> Dependents { get; set; } = new();

    /// <summary>Loop 2700 member reporting categories (LS*2700 ... LE*2700).</summary>
    [JsonPropertyName("reportingCategories")]
    public List<ReportingCategory>? ReportingCategories { get; set; }

    /// <summary>
    /// Optional caller-supplied transaction id (834 BGN02 or manual). When omitted the
    /// import service derives a deterministic id from <c>(BatchId, SubscriberId)</c>.
    /// </summary>
    [JsonPropertyName("transactionId")]
    public string? TransactionId { get; set; }

    /// <summary>
    /// For manual-source enrollments only: the client-supplied idempotency key for the
    /// resulting <see cref="EnrollmentEvent"/>. Ignored when <see cref="Enrollment834.ManualSource"/>
    /// is false. Defaults to a fresh GUID at the controller boundary.
    /// </summary>
    [JsonPropertyName("eventId")]
    public string? EventId { get; set; }
}

public class Demographics
{
    [JsonPropertyName("entityType")]
    public string? EntityType { get; set; }
    
    [JsonPropertyName("lastName")]
    public string LastName { get; set; } = string.Empty;
    
    [JsonPropertyName("firstName")]
    public string FirstName { get; set; } = string.Empty;
    
    [JsonPropertyName("middleName")]
    public string? MiddleName { get; set; }
    
    [JsonPropertyName("suffix")]
    public string? Suffix { get; set; }
    
    [JsonPropertyName("idQualifier")]
    public string? IdQualifier { get; set; }
    
    [JsonPropertyName("id")]
    public string? Id { get; set; } // SSN or Member ID
    
    [JsonPropertyName("address1")]
    public string? Address1 { get; set; }
    
    [JsonPropertyName("address2")]
    public string? Address2 { get; set; }
    
    [JsonPropertyName("city")]
    public string? City { get; set; }
    
    [JsonPropertyName("state")]
    public string? State { get; set; }
    
    [JsonPropertyName("zip")]
    public string? Zip { get; set; }
    
    [JsonPropertyName("dateOfBirth")]
    public string? DateOfBirth { get; set; }
    
    [JsonPropertyName("gender")]
    public string? Gender { get; set; } // M, F, U
}

public class Sponsor
{
    [JsonPropertyName("qualifier")]
    public string Qualifier { get; set; } = string.Empty;
    
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
    
    [JsonPropertyName("idQualifier")]
    public string? IdQualifier { get; set; }
    
    [JsonPropertyName("id")]
    public string? Id { get; set; }
}

public class CoverageDetail
{
    [JsonPropertyName("maintenanceType")]
    public string? MaintenanceType { get; set; }
    
    [JsonPropertyName("maintenanceReason")]
    public string? MaintenanceReason { get; set; }
    
    [JsonPropertyName("insuranceLineCode")]
    public string InsuranceLineCode { get; set; } = string.Empty; // HLT, DEN, VIS
    
    [JsonPropertyName("planCoverageDescription")]
    public string? PlanCoverageDescription { get; set; }
    
    [JsonPropertyName("coverageLevel")]
    public string? CoverageLevel { get; set; } // EMP, ESP, ECH, FAM

    /// <summary>Loop 2300 DTP*348 — benefit begin.</summary>
    [JsonPropertyName("benefitBeginDate")]
    public string? BenefitBeginDate { get; set; }

    /// <summary>Loop 2300 DTP*349 — benefit end.</summary>
    [JsonPropertyName("benefitEndDate")]
    public string? BenefitEndDate { get; set; }
}

/// <summary>One Loop 2700 member reporting category (LX / N1*75 / REF / DTP).</summary>
public class ReportingCategory
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("referenceQualifier")]
    public string? ReferenceQualifier { get; set; }

    [JsonPropertyName("referenceValue")]
    public string? ReferenceValue { get; set; }

    [JsonPropertyName("date")]
    public string? Date { get; set; }
}

/// <summary>
/// A dependent member — its own Loop 2000 (INS01=N) in X12 834. The
/// per-member INS/REF/DTP fields below are optional so JSON callers that
/// only send demographics keep working: when absent, the import service
/// inherits the owning subscriber's maintenance type and dates.
/// </summary>
public class Dependent
{
    /// <summary>INS02 individual relationship code (01=Spouse, 19=Child, ...).</summary>
    [JsonPropertyName("relationship")]
    public string? Relationship { get; set; }

    /// <summary>INS03 — 001=Change, 021=Addition, 024=Termination, 025=Reinstatement.</summary>
    [JsonPropertyName("maintenanceType")]
    public string? MaintenanceType { get; set; }

    [JsonPropertyName("maintenanceReason")]
    public string? MaintenanceReason { get; set; }

    [JsonPropertyName("benefitStatus")]
    public string? BenefitStatus { get; set; }

    /// <summary>REF*0F — the subscriber identifier this dependent belongs to.</summary>
    [JsonPropertyName("subscriberId")]
    public string? SubscriberId { get; set; }

    /// <summary>
    /// Member-level supplemental identifier (Loop 2000 REF*23 client
    /// number). Keys the dependent deterministically across re-imports.
    /// </summary>
    [JsonPropertyName("memberIdentifier")]
    public string? MemberIdentifier { get; set; }

    [JsonPropertyName("groupNumber")]
    public string? GroupNumber { get; set; }

    [JsonPropertyName("enrollmentDate")]
    public string? EnrollmentDate { get; set; }

    [JsonPropertyName("terminationDate")]
    public string? TerminationDate { get; set; }

    [JsonPropertyName("entityType")]
    public string? EntityType { get; set; }
    
    [JsonPropertyName("lastName")]
    public string LastName { get; set; } = string.Empty;
    
    [JsonPropertyName("firstName")]
    public string FirstName { get; set; } = string.Empty;
    
    [JsonPropertyName("middleName")]
    public string? MiddleName { get; set; }
    
    [JsonPropertyName("suffix")]
    public string? Suffix { get; set; }
    
    [JsonPropertyName("idQualifier")]
    public string? IdQualifier { get; set; }
    
    [JsonPropertyName("id")]
    public string? Id { get; set; }
    
    [JsonPropertyName("address1")]
    public string? Address1 { get; set; }
    
    [JsonPropertyName("address2")]
    public string? Address2 { get; set; }
    
    [JsonPropertyName("city")]
    public string? City { get; set; }
    
    [JsonPropertyName("state")]
    public string? State { get; set; }
    
    [JsonPropertyName("zip")]
    public string? Zip { get; set; }
    
    [JsonPropertyName("dateOfBirth")]
    public string? DateOfBirth { get; set; }
    
    [JsonPropertyName("gender")]
    public string? Gender { get; set; }
    
    [JsonPropertyName("coverage")]
    public List<CoverageDetail>? Coverage { get; set; }

    [JsonPropertyName("reportingCategories")]
    public List<ReportingCategory>? ReportingCategories { get; set; }
}

