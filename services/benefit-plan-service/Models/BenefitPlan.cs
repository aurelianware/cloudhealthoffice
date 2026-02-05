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

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("updatedAt")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

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
