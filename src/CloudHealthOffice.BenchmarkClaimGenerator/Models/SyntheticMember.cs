namespace CloudHealthOffice.BenchmarkClaimGenerator.Models;

/// <summary>
/// Represents a synthetic member/subscriber for benchmark claim generation.
/// </summary>
public class SyntheticMember
{
    /// <summary>Unique member identifier (e.g., MBR-0000001).</summary>
    public string MemberId { get; set; } = string.Empty;

    /// <summary>Subscriber/policy holder identifier.</summary>
    public string SubscriberId { get; set; } = string.Empty;

    /// <summary>Member first name.</summary>
    public string FirstName { get; set; } = string.Empty;

    /// <summary>Member last name.</summary>
    public string LastName { get; set; } = string.Empty;

    /// <summary>Date of birth.</summary>
    public DateTime DateOfBirth { get; set; }

    /// <summary>Gender code (M, F, U).</summary>
    public string Gender { get; set; } = string.Empty;

    /// <summary>Relationship to subscriber (Self, Spouse, Child, Other).</summary>
    public string Relationship { get; set; } = string.Empty;

    /// <summary>Coverage effective date.</summary>
    public DateTime CoverageEffectiveDate { get; set; }

    /// <summary>Coverage termination date (null if active).</summary>
    public DateTime? CoverageTermDate { get; set; }

    /// <summary>Benefit plan identifier.</summary>
    public string PlanId { get; set; } = string.Empty;

    /// <summary>Member state of residence (two-letter code).</summary>
    public string State { get; set; } = string.Empty;

    /// <summary>Member ZIP code.</summary>
    public string ZipCode { get; set; } = string.Empty;
}
