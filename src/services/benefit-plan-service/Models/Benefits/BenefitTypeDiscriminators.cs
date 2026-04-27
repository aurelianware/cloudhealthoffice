namespace BenefitPlanService.Models.Benefits;

/// <summary>
/// Wire values for the <c>benefitType</c> discriminator used by
/// <see cref="Benefit"/> and <see cref="BenefitPlanService.Models.AdapterBenefit"/>.
/// CamelCase by convention so the discriminator matches the rest of the
/// JSON envelope emitted by this service.
/// </summary>
public static class BenefitTypeDiscriminators
{
    public const string Medical = "medical";
    public const string Dental = "dental";
    public const string Pharmacy = "pharmacy";
    public const string BehavioralHealth = "behavioralHealth";
    public const string Vision = "vision";
    public const string DME = "dme";
    public const string Maternity = "maternity";
    public const string Preventive = "preventive";
}
