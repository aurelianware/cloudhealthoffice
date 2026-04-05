namespace FhirService.Models;

/// <summary>
/// Configuration for CRD (Coverage Requirements Discovery) service.
/// Bound from Cms0057:Crd config section.
/// Sprint 1: configuration-driven benefit lookup. Sprint 2: full BenefitEngine integration.
/// </summary>
public class CrdConfig
{
    public bool Enabled { get; set; } = true;
    public List<string> AuthRequiredCodes { get; set; } = new();
    public List<string> AutoApprovedCodes { get; set; } = new();
    public List<string> DocumentationRequiredCodes { get; set; } = new();
}
