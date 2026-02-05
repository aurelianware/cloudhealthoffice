using System;
using System.ComponentModel.DataAnnotations;

namespace ProviderService.Models;

/// <summary>
/// Represents a healthcare provider (physician, hospital, facility).
/// Used for network validation, claims adjudication, and provider directory.
/// </summary>
public class Provider
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
    /// National Provider Identifier (10-digit)
    /// Type 1 = Individual, Type 2 = Organization
    /// </summary>
    [Required]
    [StringLength(10, MinimumLength = 10)]
    [RegularExpression(@"^\d{10}$", ErrorMessage = "NPI must be 10 digits")]
    public string NPI { get; set; } = string.Empty;

    /// <summary>
    /// Provider type (Individual or Organization)
    /// </summary>
    [Required]
    public ProviderType ProviderType { get; set; }

    /// <summary>
    /// Tax Identification Number (EIN for organizations, SSN for individuals - encrypted)
    /// </summary>
    [StringLength(20)]
    public string? TaxId { get; set; }

    // Individual provider fields
    /// <summary>
    /// First name (for individual providers)
    /// </summary>
    [StringLength(100)]
    public string? FirstName { get; set; }

    /// <summary>
    /// Last name (for individual providers)
    /// </summary>
    [StringLength(100)]
    public string? LastName { get; set; }

    /// <summary>
    /// Middle name (for individual providers)
    /// </summary>
    [StringLength(100)]
    public string? MiddleName { get; set; }

    /// <summary>
    /// Professional credentials (MD, DO, NP, PA, DDS, etc.)
    /// </summary>
    [StringLength(20)]
    public string? Credentials { get; set; }

    // Organization provider fields
    /// <summary>
    /// Organization legal name (for facility/group providers)
    /// </summary>
    [StringLength(300)]
    public string? OrganizationName { get; set; }

    /// <summary>
    /// Doing Business As (DBA) name
    /// </summary>
    [StringLength(300)]
    public string? DBAName { get; set; }

    /// <summary>
    /// Primary specialty (NUCC taxonomy code)
    /// </summary>
    [Required]
    [StringLength(20)]
    public string PrimarySpecialty { get; set; } = string.Empty;

    /// <summary>
    /// Primary taxonomy code (NUCC Healthcare Provider Taxonomy)
    /// Example: 207R00000X = Internal Medicine
    /// </summary>
    [Required]
    [StringLength(10)]
    public string TaxonomyCode { get; set; } = string.Empty;

    /// <summary>
    /// Secondary specialties (taxonomy codes)
    /// </summary>
    public List<string> SecondarySpecialties { get; set; } = new();

    /// <summary>
    /// Practice address
    /// </summary>
    [StringLength(300)]
    public string? Address { get; set; }

    /// <summary>
    /// City
    /// </summary>
    [StringLength(100)]
    public string? City { get; set; }

    /// <summary>
    /// State code (2 letters)
    /// </summary>
    [StringLength(2)]
    public string? State { get; set; }

    /// <summary>
    /// ZIP code
    /// </summary>
    [StringLength(10)]
    public string? ZipCode { get; set; }

    /// <summary>
    /// Phone number
    /// </summary>
    [Phone]
    [StringLength(20)]
    public string? Phone { get; set; }

    /// <summary>
    /// Fax number
    /// </summary>
    [StringLength(20)]
    public string? Fax { get; set; }

    /// <summary>
    /// Email address
    /// </summary>
    [EmailAddress]
    [StringLength(200)]
    public string? Email { get; set; }

    /// <summary>
    /// Network participations (in-network for specific plans/LOBs)
    /// </summary>
    public List<NetworkParticipation> NetworkParticipations { get; set; } = new();

    /// <summary>
    /// Credentialing status
    /// </summary>
    [Required]
    public CredentialingStatus CredentialingStatus { get; set; } = CredentialingStatus.Pending;

    /// <summary>
    /// Credentialing date (initial or most recent re-credentialing)
    /// </summary>
    public DateTime? CredentialingDate { get; set; }

    /// <summary>
    /// Next re-credentialing due date (typically every 2-3 years)
    /// </summary>
    public DateTime? RecredentialingDueDate { get; set; }

    /// <summary>
    /// CAQH ProView ID (for credentialing data exchange)
    /// </summary>
    [StringLength(20)]
    public string? CAQHProviderId { get; set; }

    /// <summary>
    /// Last CAQH sync date
    /// </summary>
    public DateTime? LastCAQHSyncDate { get; set; }

    /// <summary>
    /// Board certifications
    /// </summary>
    public List<BoardCertification> BoardCertifications { get; set; } = new();

    /// <summary>
    /// Hospital affiliations (for admitting privileges)
    /// </summary>
    public List<HospitalAffiliation> HospitalAffiliations { get; set; } = new();

    /// <summary>
    /// Accepting new patients?
    /// </summary>
    public bool AcceptingNewPatients { get; set; } = true;

    /// <summary>
    /// Handicap accessible?
    /// </summary>
    public bool HandicapAccessible { get; set; }

    /// <summary>
    /// Languages spoken (ISO 639-1 codes: en, es, zh, etc.)
    /// </summary>
    public List<string> LanguagesSpoken { get; set; } = new() { "en" };

    /// <summary>
    /// Provider status
    /// </summary>
    [Required]
    public ProviderStatus Status { get; set; } = ProviderStatus.Active;

    /// <summary>
    /// Termination date (if deactivated)
    /// </summary>
    public DateTime? TerminationDate { get; set; }

    /// <summary>
    /// Termination reason
    /// </summary>
    [StringLength(500)]
    public string? TerminationReason { get; set; }

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
    public string FullName => ProviderType == ProviderType.Individual
        ? $"{FirstName} {MiddleName} {LastName} {Credentials}".Replace("  ", " ").Trim()
        : OrganizationName ?? "Unknown Organization";
}

/// <summary>
/// Network participation record (links provider to specific plan/LOB/network tier)
/// </summary>
public class NetworkParticipation
{
    /// <summary>
    /// Plan ID (optional - can participate at LOB level)
    /// </summary>
    [StringLength(50)]
    public string? PlanId { get; set; }

    /// <summary>
    /// Line of Business
    /// </summary>
    [Required]
    public LineOfBusiness LineOfBusiness { get; set; }

    /// <summary>
    /// Network tier (Tier 1 = lowest cost-sharing, Tier 2 = medium, Tier 3 = highest)
    /// </summary>
    [StringLength(20)]
    public string NetworkTier { get; set; } = "Tier1";

    /// <summary>
    /// Participation effective date
    /// </summary>
    [Required]
    public DateTime EffectiveDate { get; set; }

    /// <summary>
    /// Participation termination date
    /// </summary>
    public DateTime? TerminationDate { get; set; }

    /// <summary>
    /// Is provider accepting new patients in this network?
    /// </summary>
    public bool AcceptingNewPatients { get; set; } = true;

    /// <summary>
    /// Contracted rates (optional - for fee schedule reference)
    /// </summary>
    public ContractedRates? Rates { get; set; }
}

/// <summary>
/// Contracted payment rates
/// </summary>
public class ContractedRates
{
    /// <summary>
    /// Fee schedule name
    /// </summary>
    [StringLength(100)]
    public string? FeeScheduleName { get; set; }

    /// <summary>
    /// Percentage of Medicare (e.g., 1.15 = 115% of Medicare)
    /// </summary>
    public decimal? PercentOfMedicare { get; set; }

    /// <summary>
    /// Flat per-member-per-month capitation
    /// </summary>
    public decimal? PMPM { get; set; }

    /// <summary>
    /// Case rate (e.g., per pregnancy, per surgery)
    /// </summary>
    public decimal? CaseRate { get; set; }
}

/// <summary>
/// Board certification record
/// </summary>
public class BoardCertification
{
    /// <summary>
    /// Specialty (e.g., "Internal Medicine", "Cardiology")
    /// </summary>
    [Required]
    [StringLength(200)]
    public string Specialty { get; set; } = string.Empty;

    /// <summary>
    /// Certifying board (e.g., "American Board of Internal Medicine")
    /// </summary>
    [Required]
    [StringLength(200)]
    public string Board { get; set; } = string.Empty;

    /// <summary>
    /// Certification date
    /// </summary>
    public DateTime CertificationDate { get; set; }

    /// <summary>
    /// Expiration date (typically 10 years)
    /// </summary>
    public DateTime? ExpirationDate { get; set; }
}

/// <summary>
/// Hospital affiliation (admitting privileges)
/// </summary>
public class HospitalAffiliation
{
    /// <summary>
    /// Hospital NPI
    /// </summary>
    [Required]
    [StringLength(10)]
    public string HospitalNPI { get; set; } = string.Empty;

    /// <summary>
    /// Hospital name
    /// </summary>
    [Required]
    [StringLength(300)]
    public string HospitalName { get; set; } = string.Empty;

    /// <summary>
    /// Has admitting privileges?
    /// </summary>
    public bool AdmittingPrivileges { get; set; }

    /// <summary>
    /// Effective date
    /// </summary>
    public DateTime EffectiveDate { get; set; }

    /// <summary>
    /// Termination date
    /// </summary>
    public DateTime? TerminationDate { get; set; }
}

/// <summary>
/// Provider type (NPI Type 1 vs Type 2)
/// </summary>
public enum ProviderType
{
    /// <summary>
    /// Individual provider (physician, NP, PA, etc.)
    /// </summary>
    Individual = 1,

    /// <summary>
    /// Organization (hospital, clinic, group practice, DME supplier)
    /// </summary>
    Organization = 2
}

/// <summary>
/// Credentialing status
/// </summary>
public enum CredentialingStatus
{
    /// <summary>
    /// Application submitted, under review
    /// </summary>
    Pending = 1,

    /// <summary>
    /// Credentialing approved, can participate
    /// </summary>
    Approved = 2,

    /// <summary>
    /// Credentialing denied
    /// </summary>
    Denied = 3,

    /// <summary>
    /// Re-credentialing required (expired)
    /// </summary>
    Expired = 4,

    /// <summary>
    /// Suspended (quality issues, fraud, etc.)
    /// </summary>
    Suspended = 5
}

/// <summary>
/// Provider status
/// </summary>
public enum ProviderStatus
{
    /// <summary>
    /// Active and participating
    /// </summary>
    Active = 1,

    /// <summary>
    /// Temporarily inactive (leave of absence)
    /// </summary>
    Inactive = 2,

    /// <summary>
    /// Terminated from network
    /// </summary>
    Terminated = 3,

    /// <summary>
    /// Pending activation
    /// </summary>
    Pending = 4
}

/// <summary>
/// Line of Business enum (matches other services)
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
