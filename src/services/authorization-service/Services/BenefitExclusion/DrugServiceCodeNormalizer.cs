using AuthorizationService.Models;

namespace AuthorizationService.Services.BenefitExclusion;

/// <summary>
/// Normalizes a requested drug/service identity so exclusion matching is
/// deterministic regardless of formatting. NDC values are commonly written with
/// hyphens (5-4-2 / 5-3-2 segmenting) and inconsistent casing; other systems
/// vary only in case and surrounding whitespace. Normalization never invents a
/// code — it only trims, upper-cases, and (for NDC) removes segment separators.
/// </summary>
public static class DrugServiceCodeNormalizer
{
    public static string Normalize(DrugServiceCodeSystem system, string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return string.Empty;

        var trimmed = code.Trim().ToUpperInvariant();

        return system == DrugServiceCodeSystem.Ndc
            ? trimmed.Replace("-", string.Empty).Replace(" ", string.Empty)
            : trimmed;
    }

    /// <summary>
    /// True when two codes refer to the same drug/service. A configured
    /// exclusion with <see cref="DrugServiceCodeSystem.Unspecified"/> matches a
    /// requested code in any system (code equality only); otherwise the systems
    /// must agree unless the request left its system unspecified.
    /// </summary>
    public static bool Matches(
        DrugServiceCodeSystem exclusionSystem, string exclusionCode,
        DrugServiceCodeSystem requestedSystem, string requestedCode)
    {
        var systemsCompatible =
            exclusionSystem == DrugServiceCodeSystem.Unspecified
            || requestedSystem == DrugServiceCodeSystem.Unspecified
            || exclusionSystem == requestedSystem;
        if (!systemsCompatible) return false;

        // Normalize BOTH sides under the more specific system so system-specific
        // formatting (e.g. NDC hyphen stripping) still applies when one side
        // leaves its system unspecified — otherwise an unhyphenated NDC exclusion
        // would miss a hyphenated request that omitted its system hint.
        var effectiveSystem = exclusionSystem != DrugServiceCodeSystem.Unspecified
            ? exclusionSystem
            : requestedSystem;
        var left = Normalize(effectiveSystem, exclusionCode);
        var right = Normalize(effectiveSystem, requestedCode);
        return left.Length > 0 && string.Equals(left, right, StringComparison.Ordinal);
    }
}
