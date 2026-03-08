using CloudHealthOffice.BenefitEngine.Domain;
using CloudHealthOffice.BenefitEngine.Models;

namespace CloudHealthOffice.BenefitEngine.Services;

// ═══════════════════════════════════════════════════════════════════
// PROVIDER INTERFACES
//
// These abstractions are the "mode toggle" between:
//   - CHO-native mode: backed by CHO's own MongoDB/Cosmos collections
//   - QNXT adapter mode: backed by QNXT API calls or DB extracts
//
// The benefit calculation engine depends only on these interfaces.
// The DI container resolves the appropriate implementation based
// on tenant configuration.
//
// To add a new core admin backend (FACETS, HealthEdge, etc.),
// implement these interfaces for that system's data model.
// ═══════════════════════════════════════════════════════════════════

/// <summary>
/// Provides the current tenant identity to engine services.
///
/// Implement this in the host service and register it as scoped DI.
/// Typical implementation reads TenantId from the HTTP request context
/// (set by the tenant middleware) or from the Argo workflow step context.
/// </summary>
public interface IBenefitEngineTenantContext
{
    string TenantId { get; }
}

/// <summary>
/// Provides benefit plan configuration to the calculation engine.
///
/// CHO-native implementation: reads from benefit-plan-service's MongoDB.
/// QNXT adapter implementation: reads from QNXT Plan/BenefitPlanDetail tables.
/// </summary>
public interface IBenefitPlanProvider
{
    /// <summary>
    /// Load the complete plan configuration (plan-level caps + all benefit
    /// categories + cost-sharing rules) needed for adjudication.
    /// </summary>
    Task<BenefitPlanConfig?> GetPlanAsync(Guid benefitPlanId, CancellationToken ct = default);
}

/// <summary>
/// Manages accumulator state (deductible, OOP, visit counts, etc.).
///
/// CHO-native: reads/writes CHO's accumulator collection.
/// QNXT adapter: reads from QNXT AccumBalance tables; optionally
/// shadow-writes to CHO for portal/analytics use.
/// </summary>
public interface IAccumulatorService
{
    /// <summary>
    /// Load current accumulator state for a member in a plan year.
    /// </summary>
    Task<IReadOnlyList<AccumulatorSnapshot>> GetAccumulatorsAsync(
        string memberId, string subscriberId,
        Guid benefitPlanId, string planYear,
        CancellationToken ct = default);

    /// <summary>
    /// Persist accumulator updates from a completed adjudication.
    /// Uses optimistic concurrency and claimId-based idempotency to
    /// handle simultaneous claims and workflow retries safely.
    /// </summary>
    Task ApplyUpdatesAsync(
        string memberId, string subscriberId,
        Guid benefitPlanId, string planYear,
        string claimId,
        IReadOnlyList<AccumulatorUpdate> updates,
        CancellationToken ct = default);

    /// <summary>
    /// Reverse accumulator entries for a voided/adjusted claim.
    /// </summary>
    Task ReverseAsync(
        string memberId, string subscriberId,
        Guid benefitPlanId, string planYear,
        string claimId,
        CancellationToken ct = default);

    /// <summary>
    /// Reset all accumulators for a plan year (annual reset batch job).
    /// </summary>
    Task ResetForPlanYearAsync(
        Guid benefitPlanId, string planYear,
        CancellationToken ct = default);
}

// ═══════════════════════════════════════════════════════════════════
// CONFIGURATION RECORDS
//
// These are the "flattened" configuration objects that the engine
// consumes. They're produced by the IBenefitPlanProvider from
// whatever backing store is configured.
// ═══════════════════════════════════════════════════════════════════

/// <summary>
/// Complete plan configuration needed for a single adjudication.
/// </summary>
public record BenefitPlanConfig
{
    public Guid Id { get; init; }
    public string TenantId { get; init; } = default!;
    public string PlanName { get; init; } = default!;
    public PlanType PlanType { get; init; }
    public string? PlanYear { get; init; }
    public string? LineOfBusiness { get; init; }

    // Accumulator caps — in-network
    public decimal? IndividualDeductible { get; init; }
    public decimal? FamilyDeductible { get; init; }
    public decimal? IndividualOopMax { get; init; }
    public decimal? FamilyOopMax { get; init; }

    // Accumulator caps — out-of-network (if different)
    public decimal? IndividualDeductibleOon { get; init; }
    public decimal? FamilyDeductibleOon { get; init; }
    public decimal? IndividualOopMaxOon { get; init; }
    public decimal? FamilyOopMaxOon { get; init; }

    // Deductible model
    public FamilyAccumulatorModel FamilyAccumulatorModel { get; init; } = FamilyAccumulatorModel.Embedded;

    // HDHP-specific
    public bool IsHdhp { get; init; }

    /// <summary>
    /// Service type codes exempt from deductible in HDHP plans
    /// (typically preventive services per ACA).
    /// </summary>
    public HashSet<string> HdhpDeductibleExemptServices { get; init; } = [];

    // Benefit categories
    public List<BenefitCategoryConfig> Categories { get; init; } = [];

    // Cross-reference
    public string? QnxtPlanId { get; init; }

    /// <summary>
    /// Look up a benefit category by service type code.
    /// </summary>
    public BenefitCategoryConfig? GetCategory(string serviceTypeCode)
        => Categories.FirstOrDefault(c =>
            string.Equals(c.ServiceTypeCode, serviceTypeCode, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Benefit rules for a single service category within a plan.
/// </summary>
public record BenefitCategoryConfig
{
    public string ServiceTypeCode { get; init; } = default!;
    public string ServiceTypeDescription { get; init; } = default!;
    public bool IsCovered { get; init; }
    public bool AuthRequired { get; init; }
    public bool ReferralRequired { get; init; }

    // Limits
    public int? VisitLimit { get; init; }
    public int? DayLimit { get; init; }
    public decimal? DollarLimit { get; init; }

    // Cost sharing
    public IReadOnlyList<CostShareRuleConfig> InNetworkCostSharing { get; init; } = [];
    public IReadOnlyList<CostShareRuleConfig> OutOfNetworkCostSharing { get; init; } = [];
}

/// <summary>
/// A single cost-sharing rule.
/// </summary>
public record CostShareRuleConfig
{
    public CostShareType CostShareType { get; init; }
    public decimal? CopayAmount { get; init; }
    public decimal? CoinsurancePercent { get; init; }
    public bool DeductibleApplies { get; init; }
}
