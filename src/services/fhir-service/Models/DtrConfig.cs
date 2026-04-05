namespace FhirService.Models;

/// <summary>
/// Configuration for DTR (Documentation Templates &amp; Rules) service.
/// Bound from Cms0057:Dtr config section.
/// </summary>
public class DtrConfig
{
    public bool Enabled { get; set; } = true;
    public int MaxQuestionnaireItems { get; set; } = 500;
    public int MaxResponseSizeBytes { get; set; } = 1_048_576; // 1MB
    public string DtrLaunchUrl { get; set; } = "https://cloudhealthoffice.com/dtr/launch";
}
