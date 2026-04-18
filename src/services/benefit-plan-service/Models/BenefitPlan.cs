using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace BenefitPlanService.Models;

/// <summary>
/// Represents a health insurance benefit plan
/// </summary>
public class BenefitPlan
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Tenant ID for multi-tenant isolation (partition key)
    /// </summary>
    [Required]
    [JsonPropertyName("tenantId")]
    public string TenantId { get; set; } = string.Empty;

    [Required]
    [JsonPropertyName("planId")]
    public string PlanId { get; set; } = string.Empty;

    [Required]
    [JsonPropertyName("planName")]
    public string PlanName { get; set; } = string.Empty;

    [Required]
    [JsonPropertyName("payer")]
    public string Payer { get; set; } = string.Empty;

    [Required]
    [JsonPropertyName("effectiveDate")]
    public DateTime EffectiveDate { get; set; }

    [JsonPropertyName("terminationDate")]
    public DateTime? TerminationDate { get; set; }

    [Required]
    [JsonPropertyName("planType")]
    public PlanType PlanType { get; set; }

    [JsonPropertyName("metalLevel")]
    public MetalLevel? MetalLevel { get; set; }

    /// <summary>
    /// Line of Business (Commercial, Medicare, Medicaid, Exchange)
    /// Determines regulatory requirements, benefit mandates, and network rules
    /// </summary>
    [Required]
    [JsonPropertyName("lineOfBusiness")]
    public LineOfBusiness LineOfBusiness { get; set; } = LineOfBusiness.Commercial;

    [JsonPropertyName("benefits")]
    public List<Benefit> Benefits { get; set; } = new();

    [JsonPropertyName("networkTiers")]
    public List<NetworkTier> NetworkTiers { get; set; } = new();

    [JsonPropertyName("costSharing")]
    public CostSharing CostSharing { get; set; } = new();

    /// <summary>
    /// Plan-level documents (SBC, EOC, Formulary, etc.).
    /// Inline for now; Phase 2 will migrate to member-document-service via
    /// FHIR DocumentReference (see PR #650 sibling work). Field shape is
    /// kept forward-compatible — the migration is then a data-copy.
    /// TODO(benefits-viewer-phase2): migrate to DocumentReference resources.
    /// </summary>
    [JsonPropertyName("documents")]
    public List<PlanDocumentReference> Documents { get; set; } = new();

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("updatedAt")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("createdDate")]
    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("modifiedDate")]
    public DateTime? ModifiedDate { get; set; }

    [JsonPropertyName("createdBy")]
    public string CreatedBy { get; set; } = string.Empty;

    [JsonPropertyName("isActive")]
    public bool IsActive { get; set; } = true;
}

/// <summary>
/// Individual benefit within a plan
/// </summary>
public class Benefit
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [Required]
    [JsonPropertyName("serviceCategory")]
    public string ServiceCategory { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("cptCodes")]
    public List<string> CptCodes { get; set; } = new();

    [JsonPropertyName("inNetworkCopay")]
    public decimal? InNetworkCopay { get; set; }

    [JsonPropertyName("outNetworkCopay")]
    public decimal? OutNetworkCopay { get; set; }

    [JsonPropertyName("inNetworkCoinsurance")]
    public decimal? InNetworkCoinsurance { get; set; } // e.g., 0.20 for 20%

    [JsonPropertyName("outNetworkCoinsurance")]
    public decimal? OutNetworkCoinsurance { get; set; }

    [JsonPropertyName("deductibleApplies")]
    public bool DeductibleApplies { get; set; } = true;

    /// <summary>
    /// Whether patient cost for this benefit accumulates toward the
    /// out-of-pocket maximum. Most benefits do; some (e.g. non-essential
    /// services, certain out-of-network services) don't.
    /// </summary>
    [JsonPropertyName("oopApplies")]
    public bool OopApplies { get; set; } = true;

    [JsonPropertyName("priorAuthRequired")]
    public bool PriorAuthRequired { get; set; } = false;

    [JsonPropertyName("copayAmount")]
    public decimal? CopayAmount { get; set; }

    [JsonPropertyName("coinsurancePercentage")]
    public decimal? CoinsurancePercentage { get; set; }

    [JsonPropertyName("requiresPriorAuth")]
    public bool RequiresPriorAuth { get; set; }

    [JsonPropertyName("visitLimit")]
    public int? VisitLimit { get; set; }

    [JsonPropertyName("visitLimitPeriod")]
    public string? VisitLimitPeriod { get; set; }

    [JsonPropertyName("limitations")]
    public string? Limitations { get; set; }

    [JsonPropertyName("annualMaximum")]
    public decimal? AnnualMaximum { get; set; }

    [JsonPropertyName("lifetimeMaximum")]
    public decimal? LifetimeMaximum { get; set; }
}

/// <summary>
/// Network tier (in-network, out-of-network, etc.)
/// </summary>
public class NetworkTier
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [Required]
    [JsonPropertyName("tierName")]
    public string TierName { get; set; } = string.Empty; // "In-Network", "Out-of-Network", "Preferred", etc.

    [JsonPropertyName("tierLevel")]
    public int TierLevel { get; set; } // 1 = best, 2 = second, etc.

    [JsonPropertyName("providerNpis")]
    public List<string> ProviderNpis { get; set; } = new();
}

/// <summary>
/// Cost sharing details (deductibles, out-of-pocket maximums)
/// </summary>
public class CostSharing
{
    [JsonPropertyName("individualDeductible")]
    public decimal IndividualDeductible { get; set; }

    [JsonPropertyName("familyDeductible")]
    public decimal FamilyDeductible { get; set; }

    [JsonPropertyName("individualOutOfPocketMax")]
    public decimal IndividualOutOfPocketMax { get; set; }

    [JsonPropertyName("familyOutOfPocketMax")]
    public decimal FamilyOutOfPocketMax { get; set; }

    [JsonPropertyName("inNetworkDeductible")]
    public decimal InNetworkDeductible { get; set; }

    [JsonPropertyName("outOfNetworkDeductible")]
    public decimal OutOfNetworkDeductible { get; set; }

    [JsonPropertyName("inNetworkOutOfPocketMax")]
    public decimal InNetworkOutOfPocketMax { get; set; }

    [JsonPropertyName("outOfNetworkOutOfPocketMax")]
    public decimal OutOfNetworkOutOfPocketMax { get; set; }

    [JsonPropertyName("outNetworkIndividualDeductible")]
    public decimal? OutNetworkIndividualDeductible { get; set; }

    [JsonPropertyName("outNetworkFamilyDeductible")]
    public decimal? OutNetworkFamilyDeductible { get; set; }

    [JsonPropertyName("outNetworkIndividualOutOfPocketMax")]
    public decimal? OutNetworkIndividualOutOfPocketMax { get; set; }

    [JsonPropertyName("outNetworkFamilyOutOfPocketMax")]
    public decimal? OutNetworkFamilyOutOfPocketMax { get; set; }
}

/// <summary>
/// Plan-level document (SBC, EOC, Formulary, ...).
///
/// Shape is deliberately forward-compatible with FHIR DocumentReference so
/// that the planned Phase 2 migration to member-document-service becomes a
/// data-copy rather than a model redesign. Fields map as follows:
///   Location         -> DocumentReference.content.attachment.url
///   ContentType      -> DocumentReference.content.attachment.contentType
///   Size             -> DocumentReference.content.attachment.size
///   ContentHashSha256-> DocumentReference.content.attachment.hash (base64 of sha256)
///   DocType          -> DocumentReference.type.coding
///   Version          -> DocumentReference.version
///   EffectiveDate    -> DocumentReference.date
///
/// TODO(benefits-viewer-phase2): replace inline list with references to
/// member-document-service once that lands.
/// </summary>
public class PlanDocumentReference
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [Required]
    [JsonPropertyName("docType")]
    public PlanDocumentType DocType { get; set; }

    /// <summary>
    /// Resolves to the document. Today this is an external HTTPS URL.
    /// After Phase 2, this may also be an internal reference of the
    /// form "documentreference/{id}" resolved by member-document-service.
    /// Consumers must accept both forms.
    /// </summary>
    [Required]
    [JsonPropertyName("location")]
    public string Location { get; set; } = string.Empty;

    [JsonPropertyName("contentType")]
    public string? ContentType { get; set; }

    [JsonPropertyName("size")]
    public long? Size { get; set; }

    /// <summary>
    /// SHA-256 of the document contents, hex-encoded. Optional today;
    /// required by the FHIR migration so populate when available.
    /// </summary>
    [JsonPropertyName("contentHashSha256")]
    public string? ContentHashSha256 { get; set; }

    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("effectiveDate")]
    public DateTime? EffectiveDate { get; set; }

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PlanDocumentType
{
    /// <summary>Summary of Benefits and Coverage (ACA mandated).</summary>
    SBC,
    /// <summary>Evidence of Coverage / Certificate of Coverage.</summary>
    EOC,
    /// <summary>Drug formulary.</summary>
    Formulary,
    /// <summary>Summary Plan Description (ERISA).</summary>
    SPD,
    Other
}

/// <summary>
/// Plan types
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PlanType
{
    HMO,
    PPO,
    EPO,
    POS,
    HDHP,
    Medicaid,
    Medicare,
    Commercial
}

/// <summary>
/// ACA metal levels
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MetalLevel
{
    Bronze,
    Silver,
    Gold,
    Platinum,
    Catastrophic
}

/// <summary>
/// Line of Business - determines regulatory requirements and benefit rules
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LineOfBusiness
{
    /// <summary>
    /// Commercial employer-sponsored coverage (ERISA regulated)
    /// </summary>
    Commercial = 1,

    /// <summary>
    /// Medicare Advantage (Part C) or Medicare Supplement
    /// CMS regulated, must follow Medicare coverage rules
    /// </summary>
    Medicare = 2,

    /// <summary>
    /// Medicaid Managed Care (state + federal regulated)
    /// EPSDT requirements, different benefit mandates per state
    /// </summary>
    Medicaid = 3,

    /// <summary>
    /// ACA Exchange/Marketplace individual plans
    /// QHP certification, Essential Health Benefits, metal levels required
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
