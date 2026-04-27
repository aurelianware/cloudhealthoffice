using System.Text.Json.Serialization;

namespace BenefitPlanService.Models.Benefits;

/// <summary>
/// Durable Medical Equipment benefit. Captures the fitting-required and
/// rental-vs-purchase facets that gate DME adjudication.
/// </summary>
public class DMEBenefit : Benefit
{
    [JsonPropertyName("benefitType")]
    public override string BenefitType => BenefitTypeDiscriminators.DME;

    /// <summary>True when the equipment must be fitted by a qualifying provider.</summary>
    [JsonPropertyName("requiresFitting")]
    public bool RequiresFitting { get; set; }

    /// <summary>Days within which the fitting must occur after the order.</summary>
    [JsonPropertyName("fittingPeriodDays")]
    public int? FittingPeriodDays { get; set; }

    /// <summary>True when the equipment is dispensed as a rental rather than a purchase.</summary>
    [JsonPropertyName("isRental")]
    public bool IsRental { get; set; }

    /// <summary>Rental cap in months. Null when uncapped or not a rental.</summary>
    [JsonPropertyName("maxRentalMonths")]
    public int? MaxRentalMonths { get; set; }
}
