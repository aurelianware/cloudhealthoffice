using System.Text.Json.Serialization;

namespace CloudHealthOffice.OperatingMode;

/// <summary>
/// Per-tenant operating mode configuration for each CHO engine.
/// Allows a health plan to run individual engines in Augment or Replace mode,
/// enabling a gradual migration from a legacy system to CHO.
///
/// Example configuration:
/// {
///   "tenantId": "demo-health-plan",
///   "engines": {
///     "benefitCalculation": "augment",
///     "rateResolution": "replace",
///     "ncciEdits": "replace",
///     "claimsScrubbing": "augment"
///   }
/// }
/// </summary>
public class OperatingModeConfiguration
{
    [JsonPropertyName("tenantId")]
    public string TenantId { get; set; } = string.Empty;

    [JsonPropertyName("engines")]
    public Dictionary<string, string> Engines { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("updatedAt")]
    public DateTime? UpdatedAt { get; set; }


    [JsonPropertyName("updatedBy")]
    public string? UpdatedBy { get; set; }

    /// <summary>
    /// Resolves the operating mode for a specific engine.
    /// Defaults to Replace if the engine is not explicitly configured.
    /// </summary>
    public IOperatingMode GetEngineMode(string engineName)
    {
        var mode = EngineOperatingMode.Replace;

        if (Engines.TryGetValue(engineName, out var modeStr) &&
            Enum.TryParse<EngineOperatingMode>(modeStr, ignoreCase: true, out var parsed))
        {
            mode = parsed;
        }

        return new OperatingModeInfo { Mode = mode };
    }

    /// <summary>
    /// Sets the operating mode for a specific engine.
    /// </summary>
    public void SetEngineMode(string engineName, EngineOperatingMode mode)
    {
        Engines[engineName.Trim()] = mode.ToString().ToLowerInvariant();
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Known engine names for use as constants.
    /// </summary>
    public static class EngineNames
    {
        public const string BenefitCalculation = "benefitCalculation";
        public const string RateResolution = "rateResolution";
        public const string NcciEdits = "ncciEdits";
        public const string ClaimsScrubbing = "claimsScrubbing";
        public const string CobCalculation = "cobCalculation";
        public const string RiskAdjustment = "riskAdjustment";
        public const string PriorAuthRules = "priorAuthRules";
        public const string ProviderVerification = "providerVerification";
        public const string TerminologyCrosswalk = "terminologyCrosswalk";
    }
}
