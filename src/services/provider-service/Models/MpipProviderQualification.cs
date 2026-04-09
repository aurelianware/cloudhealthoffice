using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace ProviderService.Models;

/// <summary>
/// Cosmos DB document tracking a provider's MPIP (Managed Medical Assistance
/// Physician Incentive Program) qualification status for a given FL fiscal year.
/// Partition key: <see cref="TenantId"/>.
///
/// <para><b>FL SMMC 3.0 MPIP rules:</b></para>
/// <list type="bullet">
///   <item>Specialists auto-qualify for the 106.3% Medicare multiplier for members under 21.</item>
///   <item>PCPs and OB/GYNs must meet AHCA performance benchmarks to qualify.</item>
///   <item>Qualification period runs Oct 1 – Sep 30 (FL fiscal year).</item>
///   <item>AHCA publishes the qualified provider list each October 1.</item>
/// </list>
/// </summary>
public class MpipProviderQualification
{
    /// <summary>
    /// Unique document identifier (Cosmos DB document id).
    /// </summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Multi-tenant partition key (required for Cosmos DB isolation).
    /// </summary>
    [JsonPropertyName("tenantId")]
    [Required]
    public string TenantId { get; set; } = string.Empty;

    /// <summary>
    /// Provider ID (internal identifier, links to the Provider document).
    /// </summary>
    [JsonPropertyName("providerId")]
    [Required]
    public string ProviderId { get; set; } = string.Empty;

    /// <summary>
    /// National Provider Identifier (10-digit NPI).
    /// </summary>
    [JsonPropertyName("npi")]
    [Required]
    [StringLength(10, MinimumLength = 10)]
    public string Npi { get; set; } = string.Empty;

    /// <summary>
    /// MPIP provider classification determining qualification rules.
    /// </summary>
    [JsonPropertyName("providerType")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public MpipProviderType ProviderType { get; set; }

    /// <summary>
    /// FL fiscal year qualification period (e.g. "2025-2026" = Oct 1 2025 – Sep 30 2026).
    /// </summary>
    [JsonPropertyName("qualificationPeriod")]
    [Required]
    public string QualificationPeriod { get; set; } = string.Empty;

    /// <summary>
    /// Whether this provider is qualified for the enhanced MPIP rate
    /// during the current qualification period.
    /// </summary>
    [JsonPropertyName("isQualified")]
    public bool IsQualified { get; set; }

    /// <summary>
    /// How the provider was qualified (or not) for MPIP.
    /// </summary>
    [JsonPropertyName("qualificationMethod")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public MpipQualificationMethod QualificationMethod { get; set; }

    /// <summary>
    /// Date the qualification takes effect (typically Oct 1 of the fiscal year).
    /// </summary>
    [JsonPropertyName("effectiveDate")]
    public DateTime EffectiveDate { get; set; }

    /// <summary>
    /// Date the qualification expires (typically Sep 30 of the following calendar year).
    /// </summary>
    [JsonPropertyName("expirationDate")]
    public DateTime ExpirationDate { get; set; }

    /// <summary>
    /// Rate multiplier applied to Medicare Physician Fee Schedule allowed amounts.
    /// 1.063 (106.3%) if qualified, 1.0 if not.
    /// </summary>
    [JsonPropertyName("enhancedRateMultiplier")]
    public decimal EnhancedRateMultiplier { get; set; } = 1.0m;

    /// <summary>
    /// Whether the MCO plan submitted this provider to AHCA as qualified.
    /// </summary>
    [JsonPropertyName("qualifiedByPlan")]
    public bool QualifiedByPlan { get; set; }

    /// <summary>
    /// Audit: document creation timestamp.
    /// </summary>
    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Audit: last modification timestamp.
    /// </summary>
    [JsonPropertyName("updatedAt")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// MPIP provider classification. Determines which qualification path applies.
/// </summary>
public enum MpipProviderType
{
    /// <summary>
    /// Primary Care Provider (family medicine, internal medicine, general practice, pediatrics).
    /// Must meet AHCA performance benchmarks to qualify for enhanced rates.
    /// </summary>
    PrimaryCare,

    /// <summary>
    /// Obstetrics and Gynecology.
    /// Must meet AHCA performance benchmarks to qualify for enhanced rates.
    /// </summary>
    ObGyn,

    /// <summary>
    /// All other specialties.
    /// Auto-qualifies for enhanced MPIP rates on services to members under 21.
    /// </summary>
    Specialist,

    /// <summary>
    /// Provider type that does not participate in MPIP.
    /// </summary>
    Other
}

/// <summary>
/// How a provider was qualified (or not) for MPIP enhanced rates.
/// </summary>
public enum MpipQualificationMethod
{
    /// <summary>
    /// Specialist providers auto-qualify for all members under 21.
    /// </summary>
    AutoQualified_Specialist,

    /// <summary>
    /// PCP or OB/GYN met AHCA performance benchmarks for the qualification period.
    /// </summary>
    PerformanceBenchmark,

    /// <summary>
    /// Provider does not qualify for enhanced MPIP rates.
    /// </summary>
    NotQualified
}
