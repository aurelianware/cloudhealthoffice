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
    /// Billing configuration
    /// </summary>
    public BillingInfo? BillingInfo { get; set; }

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
