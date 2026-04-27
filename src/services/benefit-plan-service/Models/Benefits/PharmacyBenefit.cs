using System.Text.Json.Serialization;

namespace BenefitPlanService.Models.Benefits;

/// <summary>
/// Pharmacy benefit. Carries formulary metadata (tier, specialty flag,
/// step-therapy gate, quantity / days-supply limits) so the pharmacy view
/// can render without inferring from <see cref="Benefit.ServiceCategory"/>
/// substring matches.
///
/// <para>
/// 5.4 establishes the shape only — the full formulary-resolution service
/// arrives in capability 5.14. Today's <c>BenefitCalculationEngine</c> reads
/// the base-class fields (Strategy A) and ignores these typed facets.
/// </para>
/// </summary>
public class PharmacyBenefit : Benefit
{
    [JsonPropertyName("benefitType")]
    public override string BenefitType => BenefitTypeDiscriminators.Pharmacy;

    /// <summary>
    /// Formulary tier label (e.g. "Tier 1", "Generic", "Preferred Brand").
    /// Free-form so payer-specific tier nomenclatures round-trip cleanly;
    /// 5.14 will introduce a canonical tier resolver.
    /// </summary>
    [JsonPropertyName("formularyTier")]
    public string? FormularyTier { get; set; }

    /// <summary>True when the drug is on a specialty-pharmacy tier.</summary>
    [JsonPropertyName("isSpecialtyDrug")]
    public bool IsSpecialtyDrug { get; set; }

    /// <summary>True when step therapy must be exhausted before coverage.</summary>
    [JsonPropertyName("requiresStepTherapy")]
    public bool RequiresStepTherapy { get; set; }

    /// <summary>Maximum dispensable quantity per fill.</summary>
    [JsonPropertyName("quantityLimit")]
    public int? QuantityLimit { get; set; }

    /// <summary>Days-supply per fill (e.g. 30, 90).</summary>
    [JsonPropertyName("daysSupply")]
    public int? DaysSupply { get; set; }
}
