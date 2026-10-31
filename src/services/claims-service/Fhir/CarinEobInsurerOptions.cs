namespace ClaimsService.Fhir;

/// <summary>
/// The plan organization an ExplanationOfBenefit names as its insurer
/// (<c>Fhir:Insurer</c>). <see cref="Reference"/> is a literal reference the
/// FHIR façade serves, such as <c>Organization/plan</c>; without it the
/// insurer is named by display only.
/// </summary>
public sealed class CarinEobInsurerOptions
{
    public const string SectionName = "Fhir:Insurer";

    public string? Reference { get; set; }

    public string? Display { get; set; }
}
