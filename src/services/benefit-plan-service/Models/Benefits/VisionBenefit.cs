using System.Text.Json.Serialization;

namespace BenefitPlanService.Models.Benefits;

/// <summary>
/// Vision benefit. Carries the routine-exam flag, frame allowance, and lens
/// coverage type that vision plans typically gate independently from
/// medical cost-sharing.
/// </summary>
public class VisionBenefit : Benefit
{
    [JsonPropertyName("benefitType")]
    public override string BenefitType => BenefitTypeDiscriminators.Vision;

    /// <summary>True when this benefit covers routine eye exams.</summary>
    [JsonPropertyName("isRoutineExam")]
    public bool IsRoutineExam { get; set; }

    /// <summary>Periodic dollar allowance toward eyeglass frames.</summary>
    [JsonPropertyName("frameAllowance")]
    public decimal? FrameAllowance { get; set; }

    /// <summary>
    /// Lens coverage classification (e.g. "Single Vision", "Bifocal",
    /// "Progressive", "Photochromic"). Free form.
    /// </summary>
    [JsonPropertyName("lensCoverageType")]
    public string? LensCoverageType { get; set; }
}
