namespace CloudHealthOffice.BenefitEngine.Domain;

// ═══════════════════════════════════════════════════════════════════
// PLAN-LEVEL CONFIGURATION
// ═══════════════════════════════════════════════════════════════════

public enum PlanType
{
    HMO,
    PPO,
    EPO,
    POS,
    HDHP,
    Indemnity
}

public enum NetworkTier
{
    InNetwork,
    OutOfNetwork,
    OutOfArea
}

public enum CostShareType
{
    Copay,
    Coinsurance,
    Deductible,
    OutOfPocketMax,
    LifetimeMax
}

/// <summary>
/// Controls how copay interacts with deductible for a given benefit category.
/// QNXT equivalent: Benefit Plan Detail → Copay Application Method
/// </summary>
public enum CopayApplicationMode
{
    /// <summary>
    /// Standard: deductible applies first, then copay on remainder.
    /// </summary>
    AfterDeductible,

    /// <summary>
    /// Copay replaces deductible — member pays a flat copay, deductible
    /// is not consumed. The copay still counts toward OOP max.
    /// Common for PCP visits, urgent care, Rx.
    /// </summary>
    InsteadOfDeductible,

    /// <summary>
    /// Copay applies in addition to deductible — member pays both.
    /// Less common; seen in some high-cost specialty tiers.
    /// </summary>
    InAdditionToDeductible
}

/// <summary>
/// Determines how cost-sharing is applied for inpatient claims.
/// QNXT equivalent: Benefit Plan Detail → Inpatient Pricing Method
/// </summary>
public enum InpatientPricingMethod
{
    /// <summary>
    /// Standard per-line adjudication.
    /// </summary>
    PerLine,

    /// <summary>
    /// DRG case rate — one cost-sharing calculation per admission.
    /// Deductible and copay apply once per admit, not per line.
    /// </summary>
    DrgCaseRate,

    /// <summary>
    /// Per diem — cost sharing applied to the per-diem total.
    /// Deductible/copay apply once per admission.
    /// </summary>
    PerDiem
}

public enum AccumulatorType
{
    IndividualDeductible,
    FamilyDeductible,
    IndividualOutOfPocketMax,
    FamilyOutOfPocketMax,
    VisitCount,
    DollarLimit,
    DayCount,
    LifetimeMax
}

public enum AccumulatorScope
{
    Individual,
    Family
}

/// <summary>
/// Current state of a single accumulator as loaded from persistent storage.
/// </summary>
public record AccumulatorSnapshot
{
    public AccumulatorType Type { get; init; }
    public AccumulatorScope Scope { get; init; }
    public NetworkTier NetworkTier { get; init; }
    public decimal LimitAmount { get; init; }
    public decimal AccumulatedAmountAfter { get; init; }
    public decimal AccumulatedAmountBefore { get; set; }
    public decimal RemainingAmount { get; set; }
    public string? ServiceTypeCode { get; init; }
}

/// <summary>
/// Deductible model.
/// </summary>
public enum FamilyAccumulatorModel
{
    /// <summary>
    /// Each member has individual deductible; family aggregate also tracked.
    /// Once individual is met, that member's deductible is satisfied.
    /// Once family aggregate is met, ALL members' deductibles are satisfied.
    /// </summary>
    Embedded,

    /// <summary>
    /// One shared deductible pool for the entire family.
    /// No individual sub-limit — the family limit is the only limit.
    /// A single member can satisfy the entire family deductible.
    /// OOP max works the same way: one family pool, no individual sub-cap.
    /// Common in HDHP plans and some Medicaid family plans.
    /// </summary>
    Aggregate
}

// ═══════════════════════════════════════════════════════════════════
// SERVICE CATEGORY MAPPING
// ═══════════════════════════════════════════════════════════════════

public class ServiceCategoryMapping
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = default!;
    public Guid? BenefitPlanId { get; set; }
    public string ServiceTypeCode { get; set; } = default!;
    public string ServiceTypeDescription { get; set; } = default!;
    public List<ProcedureCodeRule> Rules { get; set; } = [];

    // Authoring metadata (capability BP 5.6). Resolver-side filtering on
    // EffectiveStart/EffectiveEnd/IsActive is deferred — these fields are
    // additive today so authors can record window/lifecycle intent without
    // changing adjudication behavior. A future capability (BP 5.10 closer)
    // wires effective-date filtering into ServiceCategoryResolver.
    public DateOnly? EffectiveStart { get; set; }
    public DateOnly? EffectiveEnd { get; set; }
    public bool IsActive { get; set; } = true;
}

public class ProcedureCodeRule
{
    public Guid Id { get; set; }
    public int Priority { get; set; }
    public string CodeType { get; set; } = "CPT";
    public string CodePattern { get; set; } = default!;
    public string? CodeRangeEnd { get; set; }
    public string? PlaceOfServiceCode { get; set; }
    public string? RequiredModifier { get; set; }
    public string? RevenueCode { get; set; }
}

