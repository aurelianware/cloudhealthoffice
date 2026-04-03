using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace ProviderContractsService.Models;

/// <summary>
/// Master provider contract record. Represents the legal agreement between
/// the health plan and a provider/group. Payment-method-specific configuration
/// (capitation rates, FFS fee schedules) are child records referencing this
/// entity by ContractId.
///
/// QNXT analog: Contract module > Provider Contract master record.
/// </summary>
public class ProviderContract
{
    // ── Identity ──────────────────────────────────────────────────────────

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
    /// User-facing contract number. Format: CTR-{NPI}-{Year}
    /// </summary>
    [Required]
    [StringLength(50)]
    public string ContractNumber { get; set; } = string.Empty;

    // ── Provider ──────────────────────────────────────────────────────────

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
    /// Provider TIN (Tax Identification Number) — required for payment processing.
    /// SENSITIVE: Portal must mask display to last 4 digits (***-**-1234).
    /// Never return full TIN in list/search endpoints — only in single-record GET.
    /// </summary>
    [SensitiveData]
    [StringLength(9)]
    public string? ProviderTin { get; set; }

    /// <summary>
    /// Provider type (Individual physician or Organization/group)
    /// </summary>
    [Required]
    public ProviderType ProviderType { get; set; }

    // ── Agreement Scope ───────────────────────────────────────────────────

    /// <summary>
    /// Line of business this contract covers
    /// </summary>
    [Required]
    public LineOfBusiness LineOfBusiness { get; set; }

    /// <summary>
    /// Benefit plan IDs in scope. Empty = all plans in the LOB.
    /// </summary>
    public List<string> PlanIds { get; set; } = new();

    /// <summary>
    /// Payment methodology — drives which rate config children are valid.
    /// </summary>
    [Required]
    public PaymentMethodology PaymentMethodology { get; set; }

    /// <summary>
    /// Network participation status — drives network adequacy reporting.
    /// </summary>
    public NetworkParticipationStatus NetworkStatus { get; set; }
        = NetworkParticipationStatus.Participating;

    // ── Contracting Parties ───────────────────────────────────────────────

    /// <summary>
    /// Internal contract owner (department or role)
    /// </summary>
    [StringLength(200)]
    public string? ContractOwner { get; set; }

    /// <summary>
    /// External signatory name
    /// </summary>
    [StringLength(200)]
    public string? SignatoryName { get; set; }

    /// <summary>
    /// Date contract was signed
    /// </summary>
    public DateTime? SignedDate { get; set; }

    // ── Effective Dating ──────────────────────────────────────────────────

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
    /// Reason for termination
    /// </summary>
    [StringLength(500)]
    public string? TerminationReason { get; set; }

    // ── Auto-renewal ──────────────────────────────────────────────────────

    /// <summary>
    /// Whether the contract automatically renews at term end
    /// </summary>
    public bool AutoRenews { get; set; }

    /// <summary>
    /// Renewal term length in months (if AutoRenews = true)
    /// </summary>
    public int? RenewalTermMonths { get; set; }

    /// <summary>
    /// Days of notice required before termination/non-renewal
    /// </summary>
    public int? NoticeRequiredDays { get; set; }

    // ── Amendments (new in v2) ────────────────────────────────────────────

    /// <summary>
    /// Mid-term contract amendments — ordered by effective date.
    /// Each amendment captures what changed and when.
    /// </summary>
    public List<ContractAmendment> Amendments { get; set; } = new();

    // ── Lifecycle ─────────────────────────────────────────────────────────

    /// <summary>
    /// Current contract status
    /// </summary>
    [Required]
    public ProviderContractStatus Status { get; set; }
        = ProviderContractStatus.Draft;

    // ── Child Config References (denormalized for portal queries) ─────────

    /// <summary>
    /// IDs of CapitationRateConfig children referencing this contract
    /// </summary>
    public List<string> CapitationRateConfigIds { get; set; } = new();

    /// <summary>
    /// IDs of FfsRateConfig children referencing this contract
    /// </summary>
    public List<string> FfsRateConfigIds { get; set; } = new();

    // ── Audit ─────────────────────────────────────────────────────────────

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

    // ── Provider Verification ─────────────────────────────────────────────

    /// <summary>
    /// Cached integrity score from the ProviderVerificationService.
    /// Updated on verification and cached for claims-time lookup.
    /// </summary>
    public int? IntegrityScore { get; set; }
    [StringLength(50)]
    public string? IntegrityRating { get; set; }
    public DateTimeOffset? LastVerifiedAt { get; set; }
    public DateTimeOffset? NextVerificationDue { get; set; }
}

// ── Supporting Types ──────────────────────────────────────────────────────

/// <summary>
/// Tracks a mid-term amendment to the master contract.
/// Examples: rate renegotiation, scope expansion, LOB addition.
/// </summary>
public class ContractAmendment
{
    /// <summary>
    /// Unique amendment identifier
    /// </summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Date the amendment takes effect
    /// </summary>
    [Required]
    public DateTime EffectiveDate { get; set; }

    /// <summary>
    /// Type of amendment (e.g. "Rate Renegotiation", "Scope Expansion")
    /// </summary>
    [Required]
    [StringLength(100)]
    public string AmendmentType { get; set; } = string.Empty;

    /// <summary>
    /// Description of what changed
    /// </summary>
    [Required]
    [StringLength(2000)]
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Who approved the amendment
    /// </summary>
    [StringLength(200)]
    public string? ApprovedBy { get; set; }

    /// <summary>
    /// When this amendment record was created
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Payment methodology — determines which rate config children are valid
/// </summary>
public enum PaymentMethodology
{
    /// <summary>
    /// Provider is paid capitated PMPM for all covered services
    /// </summary>
    FullCapitation = 1,

    /// <summary>
    /// Provider is paid fee-for-service per rendered service
    /// </summary>
    FeeForService = 2,

    /// <summary>
    /// Mixed: some services capitated, others FFS (requires both rate config children)
    /// </summary>
    Hybrid = 3,

    /// <summary>
    /// Provider is under global risk arrangement
    /// </summary>
    GlobalRisk = 4
}

/// <summary>
/// Provider contract lifecycle status
/// </summary>
public enum ProviderContractStatus
{
    /// <summary>
    /// Contract is being drafted, not yet effective
    /// </summary>
    Draft = 1,

    /// <summary>
    /// Contract is active
    /// </summary>
    Active = 2,

    /// <summary>
    /// Contract temporarily suspended
    /// </summary>
    Suspended = 3,

    /// <summary>
    /// Contract terminated by either party
    /// </summary>
    Terminated = 4,

    /// <summary>
    /// Contract expired (past termination date)
    /// </summary>
    Expired = 5,

    /// <summary>
    /// Contract approaching term end with auto-renewal pending
    /// </summary>
    PendingRenewal = 6
}

/// <summary>
/// Network participation status — drives network adequacy reporting
/// </summary>
public enum NetworkParticipationStatus
{
    /// <summary>
    /// In-network, standard contracted rates apply
    /// </summary>
    Participating = 1,

    /// <summary>
    /// Out-of-network
    /// </summary>
    NonParticipating = 2,

    /// <summary>
    /// Tiered exception — in-network for specific services only
    /// </summary>
    TieredException = 3
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

/// <summary>
/// Marks a property as containing sensitive data (e.g. TIN, SSN).
/// Portal must mask display; list endpoints must not return full value.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public class SensitiveDataAttribute : Attribute
{
}
