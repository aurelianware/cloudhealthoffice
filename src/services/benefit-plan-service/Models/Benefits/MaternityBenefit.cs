using System.Text.Json.Serialization;

namespace BenefitPlanService.Models.Benefits;

/// <summary>
/// Maternity benefit. Splits coverage along the prenatal / delivery /
/// postpartum / NICU axes that the Newborns' and Mothers' Health Protection
/// Act and downstream analytics treat as separate episodes of care.
/// </summary>
public class MaternityBenefit : Benefit
{
    [JsonPropertyName("benefitType")]
    public override string BenefitType => BenefitTypeDiscriminators.Maternity;

    [JsonPropertyName("coversPrenatal")]
    public bool CoversPrenatal { get; set; }

    [JsonPropertyName("coversDelivery")]
    public bool CoversDelivery { get; set; }

    [JsonPropertyName("coversPostpartum")]
    public bool CoversPostpartum { get; set; }

    /// <summary>True when neonatal intensive care unit charges are covered under this benefit.</summary>
    [JsonPropertyName("coversNicu")]
    public bool CoversNICU { get; set; }
}
