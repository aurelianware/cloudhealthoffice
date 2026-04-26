using System.Text.Json.Serialization;

namespace BenefitPlanService.Models.Benefits;

/// <summary>
/// The catch-all medical benefit shape — adds no facets beyond
/// <see cref="Benefit"/>. <c>MedicalBenefit</c> is the hydration default for
/// legacy rows that predate the discriminator (<c>"benefitType"</c> missing
/// or empty), and the safe choice for any benefit that doesn't fit a more
/// specific subclass.
/// </summary>
public class MedicalBenefit : Benefit
{
    [JsonPropertyName("benefitType")]
    public override string BenefitType => BenefitTypeDiscriminators.Medical;
}
