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

    /// <summary>
    /// Launch URL of a DTR SMART app the payer runs. Empty by default: CHO ships
    /// no DTR app (decision Q-6), and providers complete forms in their EHR's own
    /// DTR client. When set, CRD documentation cards link to it with an
    /// appContext naming the forms and the coverage-assertion-id.
    /// </summary>
    public string? DtrLaunchUrl { get; set; }
}
