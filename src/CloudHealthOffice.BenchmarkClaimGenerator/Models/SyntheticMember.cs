namespace CloudHealthOffice.BenchmarkClaimGenerator.Models;

/// <summary>
/// Represents a synthetic member/subscriber for benchmark claim generation.
/// Structurally compatible with the production Member entity in member-service.
/// </summary>
public class SyntheticMember
{
    /// <summary>Unique member identifier (e.g., MCC-MBR-0000001).</summary>
    public string MemberId { get; set; } = string.Empty;

    /// <summary>Subscriber/policy holder identifier (e.g., MCC-SUB-0000001).</summary>
    public string SubscriberId { get; set; } = string.Empty;

    /// <summary>Member first name.</summary>
    public string FirstName { get; set; } = string.Empty;

    /// <summary>Member last name.</summary>
    public string LastName { get; set; } = string.Empty;

    /// <summary>Member middle name.</summary>
    public string? MiddleName { get; set; }

    /// <summary>Date of birth.</summary>
    public DateTime DateOfBirth { get; set; }

    /// <summary>Gender code (M, F, U).</summary>
    public string Gender { get; set; } = string.Empty;

    /// <summary>Relationship to subscriber (Self, Spouse, Child, Other). Human-readable label.</summary>
    public string Relationship { get; set; } = string.Empty;

    /// <summary>X12 834 relationship code (18=Self, 01=Spouse, 19=Child, 20=Employee).</summary>
    public string RelationshipCode { get; set; } = "18";

    /// <summary>Whether this member is the subscriber (primary policyholder).</summary>
    public bool IsSubscriber { get; set; } = true;

    /// <summary>Coverage effective date.</summary>
    public DateTime CoverageEffectiveDate { get; set; }

    /// <summary>Coverage termination date (null if active).</summary>
    public DateTime? CoverageTermDate { get; set; }

    /// <summary>Benefit plan identifier.</summary>
    public string PlanId { get; set; } = string.Empty;

    /// <summary>Enrollment status (Active, Terminated, Pending, Suspended, COBRA).</summary>
    public string EnrollmentStatus { get; set; } = "Active";

    /// <summary>X12 834 maintenance type code (021=Addition, 024=Cancel, 001=Change).</summary>
    public string MaintenanceTypeCode { get; set; } = "021";

    /// <summary>
    /// Retroactive effective date of a benefit-plan/coverage change (X12 834
    /// maintenance type code 001), when this member record represents a
    /// correction recorded after claims with earlier service dates may have
    /// already been submitted. Null for members with no pending retroactive
    /// change.
    /// </summary>
    public DateTime? PlanChangeEffectiveDate { get; set; }

    /// <summary>
    /// Medicaid "medically needy" spend-down liability for the member's
    /// current budget period: incurred medical expense required before
    /// Medicaid activates. Null for members not enrolled under a spend-down
    /// eligibility category.
    /// </summary>
    public decimal? MedicaidSpendDownLiabilityAmount { get; set; }

    /// <summary>Amount incurred so far toward <see cref="MedicaidSpendDownLiabilityAmount"/>.</summary>
    public decimal MedicaidSpendDownAmountMet { get; set; }

    /// <summary>Line of business (STAR, CHIP, STAR+PLUS, STAR Kids, STAR Health, Commercial, Medicare).</summary>
    public string LineOfBusiness { get; set; } = "STAR";

    /// <summary>Group number from sponsor/employer.</summary>
    public string GroupNumber { get; set; } = string.Empty;

    /// <summary>Street address line 1.</summary>
    public string Address { get; set; } = string.Empty;

    /// <summary>City.</summary>
    public string City { get; set; } = string.Empty;

    /// <summary>Member state of residence (two-letter code).</summary>
    public string State { get; set; } = string.Empty;

    /// <summary>Member ZIP code.</summary>
    public string ZipCode { get; set; } = string.Empty;

    /// <summary>Phone number.</summary>
    public string? Phone { get; set; }

    /// <summary>Email address.</summary>
    public string? Email { get; set; }

    /// <summary>Synthetic SSN-formatted identifier for test/benchmark use only; not a real member SSN.</summary>
    public string? SSN { get; set; }

    /// <summary>Primary care provider NPI assignment (for HMO-style plans).</summary>
    public string? PcpNpi { get; set; }

    /// <summary>Primary care provider name (denormalized).</summary>
    public string? PcpName { get; set; }

    /// <summary>Coverage records for this member.</summary>
    public List<SyntheticCoverage> Coverages { get; set; } = new();

    /// <summary>Dependent members (only populated on subscriber records).</summary>
    public List<SyntheticDependent> Dependents { get; set; } = new();

    /// <summary>Tenant identifier for multi-tenant Cosmos DB.</summary>
    public string TenantId { get; set; } = "mcc-benchmark";

    /// <summary>Employment start date (for subscriber).</summary>
    public DateTime? EmploymentDate { get; set; }

    /// <summary>Full name computed from first and last name.</summary>
    public string FullName => $"{FirstName} {LastName}".Trim();
}
