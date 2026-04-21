using System;
using System.ComponentModel.DataAnnotations;

namespace SponsorService.Models;

/// <summary>
/// Represents an employer/group that purchases health coverage for members.
/// Populated by X12 834 Enrollment transactions (sponsor segments).
/// </summary>
public class Sponsor
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
    /// Group number from 834 REF*1L segment
    /// </summary>
    [Required]
    [StringLength(50)]
    public string GroupNumber { get; set; } = string.Empty;

    /// <summary>
    /// Employer legal name from 834 NM1 segment
    /// </summary>
    [Required]
    [StringLength(200)]
    public string EmployerName { get; set; } = string.Empty;

    /// <summary>
    /// Employer Tax ID (EIN) from 834 REF*EI segment
    /// </summary>
    [StringLength(20)]
    public string? TaxId { get; set; }

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
    /// Primary contact name from 834 PER segment
    /// </summary>
    [StringLength(150)]
    public string? ContactName { get; set; }

    /// <summary>
    /// Contact phone from 834 PER segment
    /// </summary>
    [Phone]
    [StringLength(20)]
    public string? ContactPhone { get; set; }

    /// <summary>
    /// Contact email from 834 PER segment
    /// </summary>
    [EmailAddress]
    [StringLength(200)]
    public string? ContactEmail { get; set; }

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
    /// Current sponsor status
    /// </summary>
    [Required]
    public SponsorStatus Status { get; set; } = SponsorStatus.Active;

    /// <summary>
    /// Line of Business (Commercial, Medicare, Medicaid, Exchange)
    /// Determines regulatory requirements and benefit rules
    /// </summary>
    [Required]
    public LineOfBusiness LineOfBusiness { get; set; } = LineOfBusiness.Commercial;

    /// <summary>
    /// Billing configuration
    /// </summary>
    public BillingInfo? BillingInfo { get; set; }

    /// <summary>
    /// Broker of record for this sponsor. Null when the sponsor purchased
    /// direct (no broker). Surfaced in the portal Coverage tab's Sponsor
    /// sub-section so reps can route broker-specific questions.
    /// </summary>
    public BrokerInfo? Broker { get; set; }

    /// <summary>
    /// Current/upcoming open enrollment window. Populated by the onboarding
    /// flow (commercial) or derived from regulatory calendars (Exchange,
    /// Medicare). Null when there's no active or scheduled OE period.
    /// </summary>
    public OpenEnrollmentWindow? OpenEnrollment { get; set; }

    /// <summary>
    /// Total count of active members under this sponsor (calculated field)
    /// </summary>
    public int TotalMembers { get; set; }

    /// <summary>
    /// Total count of active dependents (calculated field)
    /// </summary>
    public int TotalDependents { get; set; }

    /// <summary>
    /// Audit: Record creation timestamp
    /// </summary>
    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Audit: Last modification timestamp
    /// </summary>
    public DateTime LastUpdatedDate { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Audit: Created by user
    /// </summary>
    [StringLength(200)]
    public string? CreatedBy { get; set; }

    /// <summary>
    /// Audit: Last updated by user
    /// </summary>
    [StringLength(200)]
    public string? LastUpdatedBy { get; set; }
}

/// <summary>
/// Sponsor lifecycle status
/// </summary>
public enum SponsorStatus
{
    /// <summary>
    /// Sponsor is active and members are covered
    /// </summary>
    Active = 1,

    /// <summary>
    /// Sponsor is temporarily suspended (non-payment, etc.)
    /// </summary>
    Suspended = 2,

    /// <summary>
    /// Sponsor contract is terminated, no active coverage
    /// </summary>
    Terminated = 3,

    /// <summary>
    /// New sponsor awaiting activation (pending setup)
    /// </summary>
    PendingActivation = 4
}

/// <summary>
/// Billing and premium configuration for sponsor
/// </summary>
public class BillingInfo
{
    /// <summary>
    /// Total monthly premium amount for all covered members
    /// </summary>
    public decimal PremiumAmount { get; set; }

    /// <summary>
    /// Billing cycle frequency
    /// </summary>
    public BillingFrequency Frequency { get; set; } = BillingFrequency.Monthly;

    /// <summary>
    /// Day of month for billing (1-31)
    /// </summary>
    [Range(1, 31)]
    public int BillingDay { get; set; } = 1;

    /// <summary>
    /// Sponsor's billing account number
    /// </summary>
    [StringLength(50)]
    public string? BillingAccountNumber { get; set; }

    /// <summary>
    /// Payment method (ACH, Wire, Check, Credit Card)
    /// </summary>
    [StringLength(50)]
    public string? PaymentMethod { get; set; }

    /// <summary>
    /// Grace period in days before suspension
    /// </summary>
    [Range(0, 90)]
    public int GracePeriodDays { get; set; } = 30;
}

/// <summary>
/// Broker/producer tied to the sponsor contract.
/// </summary>
public class BrokerInfo
{
    [StringLength(200)]
    public string? AgencyName { get; set; }

    [StringLength(200)]
    public string? Name { get; set; }

    [Phone]
    [StringLength(20)]
    public string? Phone { get; set; }

    [EmailAddress]
    [StringLength(200)]
    public string? Email { get; set; }

    /// <summary>Producer National Producer Number (optional).</summary>
    [StringLength(20)]
    public string? Npn { get; set; }
}

/// <summary>
/// Open enrollment window for the sponsor. For commercial groups this is the
/// annual OE window from the sponsor's onboarding config; for Exchange it's
/// the federal/state OE period for the current plan year.
/// </summary>
public class OpenEnrollmentWindow
{
    /// <summary>Inclusive start of the OE window.</summary>
    public DateTime Start { get; set; }

    /// <summary>Inclusive end of the OE window.</summary>
    public DateTime End { get; set; }

    /// <summary>
    /// Display-oriented status. Computed fresh by callers rather than
    /// persisted, so a stale document never claims "Open" past the end date.
    /// </summary>
    public OpenEnrollmentStatus Status(DateTime? asOf = null)
    {
        var now = asOf ?? DateTime.UtcNow;
        if (now < Start) return OpenEnrollmentStatus.Upcoming;
        if (now > End)   return OpenEnrollmentStatus.Closed;
        return OpenEnrollmentStatus.Open;
    }
}

public enum OpenEnrollmentStatus
{
    Upcoming = 0,
    Open = 1,
    Closed = 2
}

/// <summary>
/// Billing frequency options
/// </summary>
public enum BillingFrequency
{
    /// <summary>
    /// Billed monthly (most common)
    /// </summary>
    Monthly = 1,

    /// <summary>
    /// Billed quarterly (every 3 months)
    /// </summary>
    Quarterly = 3,

    /// <summary>
    /// Billed semi-annually (every 6 months)
    /// </summary>
    SemiAnnually = 6,

    /// <summary>
    /// Billed annually (once per year)
    /// </summary>
    Annual = 12
}

/// <summary>
/// Line of Business - determines regulatory requirements and benefit rules
/// </summary>
public enum LineOfBusiness
{
    /// <summary>
    /// Commercial employer-sponsored coverage (ERISA regulated)
    /// </summary>
    Commercial = 1,

    /// <summary>
    /// Medicare Advantage (Part C) or Medicare Supplement
    /// CMS regulated, 65+ or disabled
    /// </summary>
    Medicare = 2,

    /// <summary>
    /// Medicaid Managed Care (state + federal regulated)
    /// Low income, pregnant women, children, disabled
    /// </summary>
    Medicaid = 3,

    /// <summary>
    /// ACA Exchange/Marketplace individual plans
    /// QHP certification required, metal levels, subsidies
    /// </summary>
    Exchange = 4,

    /// <summary>
    /// TRICARE (military health coverage)
    /// </summary>
    TRICARE = 5,

    /// <summary>
    /// Veterans Affairs health coverage
    /// </summary>
    VA = 6
}
