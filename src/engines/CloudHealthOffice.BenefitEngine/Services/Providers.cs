using CloudHealthOffice.BenefitEngine.Domain;
using CloudHealthOffice.BenefitEngine.Models;

namespace CloudHealthOffice.BenefitEngine.Services;

// ═══════════════════════════════════════════════════════════════════
// PROVIDER INTERFACES
// ═══════════════════════════════════════════════════════════════════

public interface IBenefitEngineTenantContext
{
    string TenantId { get; }
}

public interface IBenefitPlanProvider
{
    Task<BenefitPlanConfig?> GetPlanAsync(Guid benefitPlanId, CancellationToken ct = default);
}

public interface IAccumulatorService
{
    Task<IReadOnlyList<AccumulatorSnapshot>> GetAccumulatorsAsync(
        string memberId, string subscriberId,
        Guid benefitPlanId, string planYear,
        CancellationToken ct = default);

    Task ApplyUpdatesAsync(
        string memberId, string subscriberId,
        Guid benefitPlanId, string planYear,
        string claimId,
        IReadOnlyList<AccumulatorUpdate> updates,
        CancellationToken ct = default);

    /// <summary>
    /// Reverse accumulator entries for a voided/adjusted claim.
    /// Finds all updates tagged with the given claimId and subtracts
    /// them from the current accumulator balances.
    ///
    /// Idempotent: if the claim has already been reversed, this is a no-op.
    ///
    /// QNXT equivalent: ACCUM_BALANCE reversal triggered by claim void/replace.
    /// </summary>
    Task ReverseAsync(
        string memberId, string subscriberId,
        Guid benefitPlanId, string planYear,
        string claimId,
        CancellationToken ct = default);

    Task ResetForPlanYearAsync(
        Guid benefitPlanId, string planYear,
        CancellationToken ct = default);
}

// ═══════════════════════════════════════════════════════════════════
// CONFIGURATION RECORDS
// ═══════════════════════════════════════════════════════════════════

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

    // Accumulator caps — out-of-network
    public decimal? IndividualDeductibleOon { get; init; }
    public decimal? FamilyDeductibleOon { get; init; }
    public decimal? IndividualOopMaxOon { get; init; }
    public decimal? FamilyOopMaxOon { get; init; }

    // Deductible model
    public FamilyAccumulatorModel FamilyAccumulatorModel { get; init; } = FamilyAccumulatorModel.Embedded;

    /// <summary>
    /// ACA 45 CFR §156.130 per-member individual out-of-pocket cap for
    /// the plan year. Resolved at <see cref="ChoBenefitPlanProvider"/>
    /// mapping time from the file-backed <c>IAcaLimitsProvider</c>. Only
    /// enforced in Aggregate mode (in Embedded mode the existing
    /// <see cref="IndividualOopMax"/> already constrains members).
    /// Null disables runtime enforcement; the
    /// <c>IPlanLimitValidator</c> still runs at write time.
    /// </summary>
    public decimal? AcaIndividualCap { get; init; }

    /// <summary>
    /// Gated rollout flag for Aggregate-mode ACA cap enforcement (G8).
    /// New plans published after capability 5.7 set this to true; legacy
    /// plans hydrate with false so members on existing Aggregate plans
    /// don't see surprise mid-year caps. Operators flip a legacy plan to
    /// enforced state by re-publishing the version. Transition support,
    /// not permanent legacy support — see
    /// docs/architecture/family-accumulator-models.md.
    /// </summary>
    public bool IsAcaCapEnforced { get; init; }

    // ── HDHP / HSA ──

    /// <summary>
    /// True if this is a High Deductible Health Plan (HSA-eligible).
    /// When true, deductible applies to ALL services before copay/coinsurance,
    /// except services listed in HdhpDeductibleExemptServices (ACA preventive).
    ///
    /// This overrides per-category DeductibleApplies settings: even if a
    /// category says DeductibleApplies=false, the HDHP flag forces deductible
    /// first — unless the category's service type code is in the exempt list.
    /// </summary>
    public bool IsHdhp { get; init; }

    /// <summary>
    /// Service type codes exempt from deductible in HDHP plans.
    /// Typically ACA-mandated preventive services.
    /// </summary>
    public HashSet<string> HdhpDeductibleExemptServices { get; init; } = [];

    // ── Inpatient pricing ──

    /// <summary>
    /// Default inpatient pricing method. Can be overridden per benefit category.
    /// </summary>
    public InpatientPricingMethod DefaultInpatientPricingMethod { get; init; } = InpatientPricingMethod.PerLine;

    // Benefit categories
    public List<BenefitCategoryConfig> Categories { get; init; } = [];

    // Cross-reference
    public string? QnxtPlanId { get; init; }

    public BenefitCategoryConfig? GetCategory(string serviceTypeCode)
        => Categories.FirstOrDefault(c =>
            string.Equals(c.ServiceTypeCode, serviceTypeCode, StringComparison.OrdinalIgnoreCase));
}

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

    /// <summary>
    /// Override the plan-level inpatient pricing method for this category.
    /// Null = use plan default.
    /// </summary>
    public InpatientPricingMethod? InpatientPricingMethod { get; init; }

    // Cost sharing
    public IReadOnlyList<CostShareRuleConfig> InNetworkCostSharing { get; init; } = [];
    public IReadOnlyList<CostShareRuleConfig> OutOfNetworkCostSharing { get; init; } = [];
}

public record CostShareRuleConfig
{
    public CostShareType CostShareType { get; init; }
    public decimal? CopayAmount { get; init; }
    public decimal? CoinsurancePercent { get; init; }
    public bool DeductibleApplies { get; init; }

    /// <summary>
    /// How the copay interacts with the deductible.
    /// Defaults to AfterDeductible (standard waterfall).
    /// Set to InsteadOfDeductible for "copay only, no deductible" services.
    /// </summary>
    public CopayApplicationMode CopayApplicationMode { get; init; } = CopayApplicationMode.AfterDeductible;
}
