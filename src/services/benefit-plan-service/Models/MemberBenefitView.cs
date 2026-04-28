using System.Text.Json.Serialization;

namespace BenefitPlanService.Models;

/// <summary>
/// Portal-facing projection of a benefit plan for a specific member as of a
/// specific service date. Produced by BenefitViewService; consumed by the
/// Benefits tab in MemberDetailsDialog.
/// </summary>
public class MemberBenefitView
{
    [JsonPropertyName("planId")]
    public string PlanId { get; set; } = string.Empty;

    [JsonPropertyName("planName")]
    public string PlanName { get; set; } = string.Empty;

    [JsonPropertyName("payer")]
    public string Payer { get; set; } = string.Empty;

    [JsonPropertyName("planType")]
    public string PlanType { get; set; } = string.Empty;

    [JsonPropertyName("metalLevel")]
    public string? MetalLevel { get; set; }

    [JsonPropertyName("lineOfBusiness")]
    public string LineOfBusiness { get; set; } = string.Empty;

    /// <summary>Date used to resolve the plan version.</summary>
    [JsonPropertyName("asOfDate")]
    public DateTime AsOfDate { get; set; }

    [JsonPropertyName("effectiveDate")]
    public DateTime EffectiveDate { get; set; }

    [JsonPropertyName("terminationDate")]
    public DateTime? TerminationDate { get; set; }

    /// <summary>
    /// Free-form version identifier surfaced to the UI so mid-year plan
    /// swaps are visible to the user (e.g. "2026.01", commit sha, etc.).
    /// Falls back to the plan UpdatedAt timestamp when unset.
    /// </summary>
    [JsonPropertyName("planVersion")]
    public string PlanVersion { get; set; } = string.Empty;

    /// <summary>
    /// Plan-level family accumulator pooling model (capability 5.7).
    /// Surfaced as a string ("Embedded" / "Aggregate") so portal and
    /// downstream consumers can render the model choice without taking
    /// a code dependency on the enum. See
    /// docs/architecture/family-accumulator-models.md.
    /// </summary>
    [JsonPropertyName("familyAccumulatorModel")]
    public string FamilyAccumulatorModel { get; set; } = "Embedded";

    [JsonPropertyName("costSharing")]
    public CostSharing CostSharing { get; set; } = new();

    [JsonPropertyName("categories")]
    public List<CategorizedBenefit> Categories { get; set; } = new();

    [JsonPropertyName("documents")]
    public List<PlanDocumentLink> Documents { get; set; } = new();
}

public class CategorizedBenefit
{
    /// <summary>
    /// Canonical category key from <see cref="Services.BenefitCategoryMap"/>.
    /// String-keyed (not an enum) so new buckets — especially varying
    /// pharmacy tier naming across plans — can be introduced without a
    /// wire-format break.
    /// </summary>
    [JsonPropertyName("category")]
    public string Category { get; set; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("serviceCategory")]
    public string ServiceCategory { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("inNetwork")]
    public NetworkTierBenefit InNetwork { get; set; } = new();

    [JsonPropertyName("outOfNetwork")]
    public NetworkTierBenefit? OutOfNetwork { get; set; }

    [JsonPropertyName("deductibleApplies")]
    public bool DeductibleApplies { get; set; }

    [JsonPropertyName("oopApplies")]
    public bool OopApplies { get; set; }

    [JsonPropertyName("priorAuthRequired")]
    public bool PriorAuthRequired { get; set; }

    [JsonPropertyName("visitLimit")]
    public int? VisitLimit { get; set; }

    [JsonPropertyName("visitLimitPeriod")]
    public string? VisitLimitPeriod { get; set; }

    [JsonPropertyName("annualMaximum")]
    public decimal? AnnualMaximum { get; set; }

    [JsonPropertyName("lifetimeMaximum")]
    public decimal? LifetimeMaximum { get; set; }

    [JsonPropertyName("limitations")]
    public string? Limitations { get; set; }

    /// <summary>
    /// Populated only for entries in the Pharmacy category. Lets plans
    /// carry Tier 1/2/3/4 or Generic/Preferred/Non-Preferred/Specialty
    /// without forcing a hard enum.
    /// </summary>
    [JsonPropertyName("pharmacy")]
    public PharmacyDetail? Pharmacy { get; set; }
}

public class NetworkTierBenefit
{
    [JsonPropertyName("tierName")]
    public string TierName { get; set; } = string.Empty;

    [JsonPropertyName("copay")]
    public decimal? Copay { get; set; }

    [JsonPropertyName("coinsurance")]
    public decimal? Coinsurance { get; set; }
}

public class PharmacyDetail
{
    /// <summary>
    /// The plan's original <c>ServiceCategory</c> string, trimmed only.
    /// This is the display label — never normalized, never collapsed.
    /// Null when the benefit's service category does not look like a
    /// pharmacy tier.
    /// </summary>
    [JsonPropertyName("tierLabel")]
    public string? TierLabel { get; set; }

    /// <summary>
    /// Normalized bucket for grouping and analytics (<c>Tier1</c>,
    /// <c>Tier2</c>, <c>Tier3</c>, <c>Tier4</c>, <c>Generic</c>,
    /// <c>PreferredBrand</c>, <c>NonPreferredBrand</c>, <c>Specialty</c>).
    /// Not for UI display — lossy by design.
    /// </summary>
    [JsonPropertyName("canonicalTier")]
    public string? CanonicalTier { get; set; }

    /// <summary>
    /// True when the raw service category matched "specialty"
    /// (case-insensitive).
    /// </summary>
    [JsonPropertyName("isSpecialty")]
    public bool IsSpecialty { get; set; }
}

public class PlanDocumentLink
{
    [JsonPropertyName("docType")]
    public string DocType { get; set; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("location")]
    public string Location { get; set; } = string.Empty;

    [JsonPropertyName("contentType")]
    public string? ContentType { get; set; }

    [JsonPropertyName("size")]
    public long? Size { get; set; }

    [JsonPropertyName("contentHashSha256")]
    public string? ContentHashSha256 { get; set; }

    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("effectiveDate")]
    public DateTime? EffectiveDate { get; set; }
}
