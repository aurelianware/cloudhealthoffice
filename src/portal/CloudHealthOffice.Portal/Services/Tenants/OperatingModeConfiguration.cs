namespace CloudHealthOffice.Portal.Services;

public class OperatingModeConfiguration
{
    public static readonly Dictionary<string, string> DefaultEngines = new(StringComparer.OrdinalIgnoreCase)
    {
        { "benefitCalculation", "replace" },
        { "rateResolution", "replace" },
        { "ncciEdits", "replace" },
        { "eligibilityVerification", "replace" },
        { "claimsAdjudication", "replace" }
    };

    public string TenantId { get; set; } = string.Empty;
    public Dictionary<string, string> Engines { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public DateTime? UpdatedAt { get; set; }
}
