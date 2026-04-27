using System.Text.Json.Serialization;

namespace BenefitPlanService.Models.Benefits;

/// <summary>
/// Behavioral health (mental health + substance-use disorder) benefit.
/// Carries the MHPAEA parity flag and parity-category label so the
/// non-quantitative-treatment-limitation analyser introduced in capability
/// 5.17 can reason over the plan without inferring from
/// <see cref="Benefit.ServiceCategory"/> strings.
///
/// <para>
/// Defaults are MHPAEA-safe: <see cref="IsParityProtected"/> is <c>true</c>
/// out of the box. 5.4 establishes the shape only; the full attestation
/// pipeline lands in 5.17.
/// </para>
/// </summary>
public class BehavioralHealthBenefit : Benefit
{
    [JsonPropertyName("benefitType")]
    public override string BenefitType => BenefitTypeDiscriminators.BehavioralHealth;

    /// <summary>
    /// True when this benefit is subject to MHPAEA parity rules (no more
    /// restrictive than the medical/surgical analog). Defaults to
    /// <c>true</c> because parity is the regulatory floor.
    /// </summary>
    [JsonPropertyName("isParityProtected")]
    public bool IsParityProtected { get; set; } = true;

    /// <summary>
    /// MHPAEA parity classification (e.g. "InpatientInNetwork",
    /// "OutpatientOutOfNetwork", "PrescriptionDrugs", "EmergencyCare"). Free
    /// form for now; 5.17 will introduce a canonical enum.
    /// </summary>
    [JsonPropertyName("parityCategory")]
    public string? ParityCategory { get; set; }
}
