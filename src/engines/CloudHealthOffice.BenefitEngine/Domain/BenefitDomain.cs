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
    LifetimeMax,

    /// <summary>
    /// Per-member ACA 45 CFR §156.130 individual out-of-pocket cap.
    /// Only seeded by <see cref="AccumulatorWorkingSet"/> in Aggregate
    /// mode when <c>BenefitPlanConfig.IsAcaCapEnforced</c> is true. The
    /// adjudication engine clamps each member's contribution to
    /// <c>min(family pool remaining, AcaIndividualCap remaining)</c> so a
    /// single member cannot exhaust the family OOP pool past the ACA
    /// individual ceiling. See
    /// docs/architecture/family-accumulator-models.md.
    /// </summary>
    AcaIndividualCap
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

    // Authoring metadata. ServiceCategoryResolver filters mappings by
    // these fields against the claim line's service date as of capability
    // BP 5.10 — see docs/architecture/adjudication-api-stabilization.md
    // for the inclusive-bound semantics and IsActive kill-switch posture.
    public DateOnly? EffectiveStart { get; set; }
    public DateOnly? EffectiveEnd { get; set; }
    public bool IsActive { get; set; } = true;

    // Insertion timestamp. Storage backends sort GetMappingsAsync results by
    // this field DESC so the resolver's first-match-wins iteration prefers
    // newer rows over older rows for overlapping rules — required for
    // deterministic resolution after a seeder re-apply leaves multiple seed
    // rows for the same serviceTypeCode in place. See
    // docs/architecture/service-category-mapping.md "Seed re-application".
    public DateTimeOffset CreatedAt { get; set; }
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

