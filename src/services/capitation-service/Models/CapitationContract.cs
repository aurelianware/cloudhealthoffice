using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace CapitationService.Models;

/// <summary>
/// Represents the contractual terms between the health plan and a capitated provider.
/// Defines PMPM rates, risk adjustment parameters, withhold/incentive pools,
/// and stop-loss thresholds for capitation payment calculations.
/// </summary>
public class CapitationContract
{
    /// <summary>
    /// Multi-tenant partition key (required for Cosmos DB isolation)
    /// </summary>
    [Required]
    public string TenantId { get; set; } = string.Empty;

    /// <summary>
    /// Unique identifier (Cosmos DB document id)
    /// </summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// User-facing contract number (e.g. "CAP-1234567890-2026")
    /// Format: CAP-{NPI}-{Year}
    /// </summary>
    [Required]
    [StringLength(50)]
    public string ContractNumber { get; set; } = string.Empty;

    /// <summary>
    /// Provider NPI (10-digit National Provider Identifier)
    /// </summary>
    [Required]
    [StringLength(10, MinimumLength = 10)]
    public string ProviderNPI { get; set; } = string.Empty;

    /// <summary>
    /// Denormalized provider name for display
    /// </summary>
    [StringLength(300)]
    public string ProviderName { get; set; } = string.Empty;

    /// <summary>
    /// Provider type (Individual physician or Organization/group)
    /// </summary>
    [Required]
    public ProviderType ProviderType { get; set; }

    /// <summary>
    /// Type of capitation arrangement
    /// </summary>
    [Required]
    public ContractType ContractType { get; set; }

    /// <summary>
    /// Line of business this contract covers
    /// </summary>
    [Required]
    public LineOfBusiness LineOfBusiness { get; set; }

    /// <summary>
    /// Benefit plan IDs covered by this contract (empty = all plans in the LOB)
    /// </summary>
    public List<string> PlanIds { get; set; } = new();

    /// <summary>
    /// PMPM rate tiers (age/sex/service category breakdowns)
    /// </summary>
    public List<CapitationRateTier> RateTiers { get; set; } = new();

    /// <summary>
    /// Whether rates are risk-adjusted (HCC/RAF scores applied to base PMPM)
    /// </summary>
    public bool RiskAdjusted { get; set; }

    /// <summary>
    /// Default risk score applied when member-level scores are unavailable (1.0 = average)
    /// </summary>
    public decimal DefaultRiskScore { get; set; } = 1.0m;

    /// <summary>
    /// Quality withhold percentage (e.g. 0.10 = 10% held back pending quality metrics)
    /// </summary>
    public decimal WithholdPercentage { get; set; }

    /// <summary>
    /// Incentive pool percentage — additional bonus pool beyond base capitation
    /// </summary>
    public decimal? IncentivePoolPercentage { get; set; }

    /// <summary>
    /// Per-member annual stop-loss threshold (plan absorbs costs above this amount)
    /// </summary>
    public decimal? StopLossThreshold { get; set; }

    /// <summary>
    /// Aggregate annual stop-loss for the entire contract
    /// </summary>
    public decimal? AggregateStopLoss { get; set; }

    /// <summary>
    /// Contract effective date
    /// </summary>
    [Required]
    public DateTime EffectiveDate { get; set; }

    /// <summary>
    /// Contract termination date (null = open-ended)
    /// </summary>
    public DateTime? TerminationDate { get; set; }

    /// <summary>
    /// Current contract status
    /// </summary>
    [Required]
    public CapitationContractStatus Status { get; set; } = CapitationContractStatus.Draft;

    /// <summary>
    /// Record creation timestamp
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Last update timestamp
    /// </summary>
    public DateTime LastUpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Created by user/system
    /// </summary>
    [StringLength(200)]
    public string? CreatedBy { get; set; }

    /// <summary>
    /// Last updated by user/system
    /// </summary>
    [StringLength(200)]
    public string? LastUpdatedBy { get; set; }
}

/// <summary>
/// A single PMPM rate tier within a capitation contract.
/// Rates are segmented by age range, gender, and service category
/// to reflect actuarial cost differences across member demographics.
/// </summary>
public class CapitationRateTier
{
    /// <summary>
    /// Tier display name (e.g. "Adult Male 18-34 Professional")
    /// </summary>
    [Required]
    [StringLength(100)]
    public string TierName { get; set; } = string.Empty;

    /// <summary>
    /// Minimum age for this tier (inclusive)
    /// </summary>
    public int AgeFrom { get; set; }

    /// <summary>
    /// Maximum age for this tier (inclusive)
    /// </summary>
    public int AgeTo { get; set; }

    /// <summary>
    /// Gender filter (null = any gender)
    /// </summary>
    [StringLength(1)]
    public string? Gender { get; set; }

    /// <summary>
    /// Age/sex actuarial category
    /// </summary>
    public AgeSexCategory? AgeSexCategory { get; set; }

    /// <summary>
    /// Base per-member-per-month rate before risk adjustment
    /// </summary>
    [Required]
    public decimal BasePMPM { get; set; }

    /// <summary>
    /// Service category this tier covers (e.g. "Professional", "Institutional", "Pharmacy")
    /// </summary>
    [StringLength(100)]
    public string? ServiceCategory { get; set; }
}

/// <summary>
/// Capitation contract type — defines the scope of services covered
/// </summary>
public enum ContractType
{
    /// <summary>
    /// Full/global capitation — all services (professional + institutional + ancillary)
    /// </summary>
    GlobalCapitation = 1,

    /// <summary>
    /// Professional services only (office visits, outpatient procedures)
    /// </summary>
    ProfessionalOnly = 2,

    /// <summary>
    /// Institutional/facility services only (inpatient, ER, observation)
    /// </summary>
    InstitutionalOnly = 3,

    /// <summary>
    /// Behavioral health services (mental health + substance use)
    /// </summary>
    BehavioralHealth = 4,

    /// <summary>
    /// Primary care services only (PCP office visits, preventive care)
    /// </summary>
    PrimaryCareOnly = 5,

    /// <summary>
    /// Specialty care services
    /// </summary>
    SpecialtyCare = 6
}

/// <summary>
/// Actuarial age/sex categories for PMPM rate tiering.
/// Standard groupings used in capitation rate development.
/// </summary>
public enum AgeSexCategory
{
    /// <summary>Age 0-1, any gender</summary>
    Infant_0_1 = 1,

    /// <summary>Age 2-11, any gender</summary>
    Child_2_11 = 2,

    /// <summary>Age 12-17, any gender</summary>
    Adolescent_12_17 = 3,

    /// <summary>Male, age 18-34</summary>
    AdultMale_18_34 = 4,

    /// <summary>Male, age 35-44</summary>
    AdultMale_35_44 = 5,

    /// <summary>Male, age 45-54</summary>
    AdultMale_45_54 = 6,

    /// <summary>Male, age 55-64</summary>
    AdultMale_55_64 = 7,

    /// <summary>Female, age 18-34</summary>
    AdultFemale_18_34 = 8,

    /// <summary>Female, age 35-44</summary>
    AdultFemale_35_44 = 9,

    /// <summary>Female, age 45-54</summary>
    AdultFemale_45_54 = 10,

    /// <summary>Female, age 55-64</summary>
    AdultFemale_55_64 = 11,

    /// <summary>Age 65+, any gender</summary>
    Senior_65Plus = 12
}

/// <summary>
/// Capitation contract lifecycle status
/// </summary>
public enum CapitationContractStatus
{
    /// <summary>
    /// Contract is being drafted, not yet effective
    /// </summary>
    Draft = 1,

    /// <summary>
    /// Contract is active and generating capitation payments
    /// </summary>
    Active = 2,

    /// <summary>
    /// Contract temporarily suspended (e.g. quality issues, credentialing lapse)
    /// </summary>
    Suspended = 3,

    /// <summary>
    /// Contract terminated by either party
    /// </summary>
    Terminated = 4,

    /// <summary>
    /// Contract expired (past termination date)
    /// </summary>
    Expired = 5
}

/// <summary>
/// Provider type (matches ProviderService.Models.ProviderType)
/// </summary>
public enum ProviderType
{
    /// <summary>
    /// Individual provider (physician, NP, PA, etc.)
    /// </summary>
    Individual = 1,

    /// <summary>
    /// Organization (hospital, clinic, group practice)
    /// </summary>
    Organization = 2
}

/// <summary>
/// Line of Business (matches other CHO services)
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
