using System.Text.Json.Serialization;

namespace BenefitPlanService.Models.Benefits;

/// <summary>
/// Dental benefit. Adds the orthodontic / implant flags and the
/// orthodontic-specific lifetime maximum that dental plans typically carry
/// alongside the shared <see cref="Benefit"/> facets.
/// </summary>
public class DentalBenefit : Benefit
{
    [JsonPropertyName("benefitType")]
    public override string BenefitType => BenefitTypeDiscriminators.Dental;

    /// <summary>True when this benefit covers orthodontic services.</summary>
    [JsonPropertyName("isOrthodontic")]
    public bool IsOrthodontic { get; set; }

    /// <summary>True when this benefit covers dental implants.</summary>
    [JsonPropertyName("isImplant")]
    public bool IsImplant { get; set; }

    /// <summary>
    /// Orthodontic-specific lifetime maximum. Distinct from
    /// <see cref="Benefit.LifetimeMaximum"/> (which is the medical lifetime cap)
    /// so dental benefits can carry both without collision.
    /// </summary>
    [JsonPropertyName("lifetimeBenefitMaximum")]
    public decimal? LifetimeBenefitMaximum { get; set; }
}
