using System.Text.Json.Serialization;

namespace BenefitPlanService.Models.Benefits;

/// <summary>
/// Preventive-care benefit. Carries the ACA preventive flag and the USPSTF
/// recommendation grade that determine whether the service is subject to
/// zero-cost-share rules.
///
/// <para>
/// 5.4 establishes the shape only — the ACA zero-cost-share calculation
/// arrives in capability 5.7 / 5.16, where the <c>BenefitCalculationEngine</c>
/// will check <see cref="IsAcaPreventive"/> and the recommendation grade
/// (A or B = full coverage, no member liability).
/// </para>
/// </summary>
public class PreventiveBenefit : Benefit
{
    [JsonPropertyName("benefitType")]
    public override string BenefitType => BenefitTypeDiscriminators.Preventive;

    /// <summary>
    /// True when this benefit qualifies under the ACA preventive-services
    /// mandate (zero member cost-share, in-network, when delivered per
    /// USPSTF / HRSA / ACIP guidelines).
    /// </summary>
    [JsonPropertyName("isAcaPreventive")]
    public bool IsAcaPreventive { get; set; }

    /// <summary>
    /// USPSTF recommendation grade: <c>"A"</c>, <c>"B"</c>, or any other
    /// label (C, D, I, or payer-specific). A and B are the grades that
    /// trigger ACA zero-cost-share.
    /// </summary>
    [JsonPropertyName("uspstfRecommendationGrade")]
    public string? UspstfRecommendationGrade { get; set; }
}
