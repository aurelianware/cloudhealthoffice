namespace CloudHealthOffice.BenefitEngine.Domain;

// ═══════════════════════════════════════════════════════════════════
// PLAN-LEVEL CONFIGURATION
// These extend the existing BenefitPlan/BenefitCategory/CostShareRule
// entities already in the repo. The engine consumes them; it doesn't
// own the CRUD — that stays in benefit-plan-service.
// ═══════════════════════════════════════════════════════════════════

/// <summary>
/// Plan type drives default behaviors (referral requirements, network restrictions, etc.).
/// </summary>
public enum PlanType
{
    HMO,    // Requires PCP, referrals, in-network only (except emergency)
    PPO,    // No referrals, in-network and out-of-network (different cost share)
    EPO,    // No referrals, in-network only
    POS,    // PCP required, referrals for specialists, some out-of-network
    HDHP,   // High deductible, HSA eligible, deductible applies before most services
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
/// Deductible model — "embedded" means each family member has their own
/// sub-limit within the family deductible. "Aggregate" means the family
/// deductible is one shared pool.
///
/// QNXT equivalent: Benefit Plan Detail → Deductible Type
/// </summary>
public enum FamilyAccumulatorModel
{
    /// <summary>
    /// Each member has individual deductible; family aggregate also tracked.
    /// Once individual is met, that member's deductible is satisfied.
    /// Once family aggregate is met, ALL members' deductibles are satisfied.
    /// Most common in commercial PPO/HMO plans.
    /// </summary>
    Embedded,

    /// <summary>
    /// One shared deductible pool for the entire family.
    /// Any member's claims contribute to the single pool.
    /// Less common; seen in some HDHP plans.
    /// </summary>
    Aggregate
}

// ═══════════════════════════════════════════════════════════════════
// SERVICE CATEGORY MAPPING
// Maps procedure codes to benefit categories. This is the "glue"
// between a claim line (which has CPT/HCPCS codes) and the benefit
// structure (which is organized by service type codes).
//
// QNXT equivalent: Service Category / Procedure Code Cross-Reference
// ═══════════════════════════════════════════════════════════════════

/// <summary>
/// Maps procedure code ranges to benefit service type codes.
/// A plan can override the default mappings.
/// </summary>
public class ServiceCategoryMapping
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = default!;

    /// <summary>
    /// Optional — if null, this is a system-wide default mapping.
    /// If set, this is a plan-specific override.
    /// </summary>
    public Guid? BenefitPlanId { get; set; }

    /// <summary>
    /// The target service type code in the benefit structure.
    /// Examples: "1" (Medical Care), "2" (Surgical), "4" (Diagnostic X-Ray),
    /// "48" (Hospital Inpatient), "50" (Hospital Outpatient), "98" (Professional Physician Visit)
    /// </summary>
    public string ServiceTypeCode { get; set; } = default!;
    public string ServiceTypeDescription { get; set; } = default!;

    /// <summary>
    /// Matching rules. Evaluated in priority order; first match wins.
    /// </summary>
    public List<ProcedureCodeRule> Rules { get; set; } = [];
}

/// <summary>
/// A single matching rule within a service category mapping.
/// Supports exact codes, ranges, and wildcard prefixes.
/// </summary>
public class ProcedureCodeRule
{
    public Guid Id { get; set; }

    /// <summary>
    /// Priority for evaluation order (lower = higher priority).
    /// </summary>
    public int Priority { get; set; }

    /// <summary>
    /// Code type: CPT, HCPCS, Revenue, CDT
    /// </summary>
    public string CodeType { get; set; } = "CPT";

    /// <summary>
    /// Exact match, range start, or prefix (with wildcard).
    /// Examples: "99213", "99201-99215", "992*"
    /// </summary>
    public string CodePattern { get; set; } = default!;

    /// <summary>
    /// Optional range end. If set, CodePattern is the range start.
    /// </summary>
    public string? CodeRangeEnd { get; set; }

    /// <summary>
    /// Optional place of service filter (e.g., "21" = Inpatient, "11" = Office).
    /// If null, applies to all POS.
    /// </summary>
    public string? PlaceOfServiceCode { get; set; }

    /// <summary>
    /// Optional modifier filter. If set, this rule only applies when the
    /// specified modifier is present on the claim line.
    /// </summary>
    public string? RequiredModifier { get; set; }

    /// <summary>
    /// Optional revenue code filter (for institutional claims).
    /// </summary>
    public string? RevenueCode { get; set; }
}
