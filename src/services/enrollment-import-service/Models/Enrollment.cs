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

    [JsonPropertyName("enrollments")]
    public List<MemberEnrollment> Enrollments { get; set; } = new();

    /// <summary>
    /// Optional caller-supplied batch id. When set, replays of the same batch produce
    /// deterministic event ids and de-duplicate at the event store.
    /// </summary>
    [JsonPropertyName("batchId")]
    public string? BatchId { get; set; }

    /// <summary>True when this batch came from a manual entry endpoint, not an 834 file.</summary>
    [JsonPropertyName("manualSource")]
    public bool ManualSource { get; set; }
}

public class MemberEnrollment
{
    [JsonPropertyName("relationship")]
    public string Relationship { get; set; } = string.Empty; // 18=Employee, 01=Spouse, 19=Child
    
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
    
    [JsonPropertyName("terminationDate")]
    public string? TerminationDate { get; set; }
    
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
}

public class Dependent
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
}

