namespace CloudHealthOffice.BenchmarkClaimGenerator.Models;

/// <summary>
/// Represents a dependent member (spouse, child) linked to a subscriber.
/// </summary>
public class SyntheticDependent
{
    /// <summary>Unique member identifier for this dependent (e.g., MCC-MBR-000000102).</summary>
    public string MemberId { get; set; } = string.Empty;

    /// <summary>Link to the subscriber's MemberId.</summary>
    public string SubscriberMemberId { get; set; } = string.Empty;

    /// <summary>Link to the subscriber's SubscriberId.</summary>
    public string SubscriberId { get; set; } = string.Empty;

    /// <summary>First name.</summary>
    public string FirstName { get; set; } = string.Empty;

    /// <summary>Last name (typically matches subscriber).</summary>
    public string LastName { get; set; } = string.Empty;

    /// <summary>Date of birth.</summary>
    public DateTime DateOfBirth { get; set; }

    /// <summary>Gender code (M, F, U).</summary>
    public string Gender { get; set; } = string.Empty;

    /// <summary>X12 834 relationship code (01=Spouse, 19=Child).</summary>
    public string RelationshipCode { get; set; } = "19";

    /// <summary>Human-readable relationship label.</summary>
    public string Relationship { get; set; } = "Child";

    /// <summary>Enrollment status (Active, Terminated).</summary>
    public string EnrollmentStatus { get; set; } = "Active";

    /// <summary>Coverage records for this dependent.</summary>
    public List<SyntheticCoverage> Coverages { get; set; } = new();

    /// <summary>Street address (typically matches subscriber).</summary>
    public string Address { get; set; } = string.Empty;

    /// <summary>City.</summary>
    public string City { get; set; } = string.Empty;

    /// <summary>State (two-letter code).</summary>
    public string State { get; set; } = string.Empty;

    /// <summary>ZIP code.</summary>
    public string ZipCode { get; set; } = string.Empty;

    /// <summary>Phone number.</summary>
    public string? Phone { get; set; }

    /// <summary>Synthetic SSN-formatted identifier for test/benchmark use only; not a real SSN.</summary>
    public string? SSN { get; set; }

    /// <summary>Tenant identifier.</summary>
    public string TenantId { get; set; } = "mcc-benchmark";

    /// <summary>Full name computed from first and last name.</summary>
    public string FullName => $"{FirstName} {LastName}".Trim();
}
