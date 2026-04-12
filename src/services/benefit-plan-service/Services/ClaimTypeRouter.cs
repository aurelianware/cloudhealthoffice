using CloudHealthOffice.OperatingMode;

namespace BenefitPlanService.Services;

/// <summary>
/// Determines routing for a claim based on its type, line of business,
/// and the tenant's operating mode configuration.
///
/// Routing decisions:
///   ChoReplace   — CHO adjudicates, result is authoritative
///   ChoAugment   — CHO adjudicates in shadow alongside QNXT, QNXT is authoritative
///   LegacyOnly   — Route to QNXT (ICoreAdminAdapter), CHO does not process
///
/// The router uses a compound key: "{claimType}-{lobName}" to look up
/// the operating mode (for example, "professional-medicaid"). If no specific
/// key is configured, falls back to the engine-level default.
/// </summary>
public interface IClaimTypeRouter
{
    /// <summary>
    /// Determine how a claim should be routed through the adjudication pipeline.
    /// </summary>
    ClaimTypeRoutingDecision Route(
        OperatingModeConfiguration config,
        string claimType,
        int? lineOfBusiness);
}

public class ClaimTypeRouter : IClaimTypeRouter
{
    public ClaimTypeRoutingDecision Route(
        OperatingModeConfiguration config,
        string claimType,
        int? lineOfBusiness)
    {
        var lobName = ResolveLineOfBusinessName(lineOfBusiness);
        var compoundKey = $"{claimType.ToLowerInvariant()}-{lobName}";

        // Check compound key first: e.g., "professional-medicaid" → augment
        if (config.Engines.TryGetValue(compoundKey, out var compoundMode))
        {
            return ParseDecision(compoundMode, compoundKey);
        }

        // Fall back to claim type: e.g., "professional" → replace
        if (config.Engines.TryGetValue(claimType.ToLowerInvariant(), out var typeMode))
        {
            return ParseDecision(typeMode, claimType.ToLowerInvariant());
        }

        // Fall back to engine-level benefitCalculation mode
        var engineMode = config.GetEngineMode(OperatingModeConfiguration.EngineNames.BenefitCalculation);
        return new ClaimTypeRoutingDecision
        {
            Route = engineMode.Mode == EngineOperatingMode.Augment
                ? AdjudicationRoute.ChoAugment
                : AdjudicationRoute.ChoReplace,
            ResolvedKey = OperatingModeConfiguration.EngineNames.BenefitCalculation,
            OperatingMode = engineMode
        };
    }

    private static ClaimTypeRoutingDecision ParseDecision(string modeStr, string resolvedKey)
    {
        if (string.Equals(modeStr, "legacy", StringComparison.OrdinalIgnoreCase))
        {
            return new ClaimTypeRoutingDecision
            {
                Route = AdjudicationRoute.LegacyOnly,
                ResolvedKey = resolvedKey,
                OperatingMode = new OperatingModeInfo { Mode = EngineOperatingMode.Replace }
            };
        }

        var isAugment = Enum.TryParse<EngineOperatingMode>(modeStr, ignoreCase: true, out var parsed)
            && parsed == EngineOperatingMode.Augment;

        return new ClaimTypeRoutingDecision
        {
            Route = isAugment ? AdjudicationRoute.ChoAugment : AdjudicationRoute.ChoReplace,
            ResolvedKey = resolvedKey,
            OperatingMode = new OperatingModeInfo { Mode = isAugment ? EngineOperatingMode.Augment : EngineOperatingMode.Replace }
        };
    }

    private static string ResolveLineOfBusinessName(int? lob) => lob switch
    {
        1 => "commercial",
        2 => "medicare",
        3 => "medicaid",
        4 => "chip",
        5 => "exchange",
        _ => "other"
    };
}

public record ClaimTypeRoutingDecision
{
    public AdjudicationRoute Route { get; init; }
    public string ResolvedKey { get; init; } = string.Empty;
    public IOperatingMode OperatingMode { get; init; } = new OperatingModeInfo { Mode = EngineOperatingMode.Replace };
}

public enum AdjudicationRoute
{
    /// <summary>CHO is authoritative. Legacy system not consulted.</summary>
    ChoReplace,

    /// <summary>CHO runs in shadow alongside QNXT. QNXT result is authoritative.</summary>
    ChoAugment,

    /// <summary>Route to legacy system only. CHO does not process this claim type.</summary>
    LegacyOnly
}
