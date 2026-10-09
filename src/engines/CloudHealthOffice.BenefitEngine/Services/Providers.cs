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

    /// <summary>
    /// The still-active (not reversed) accumulator updates
    /// <paramref name="claimId"/> already applied, if any. The engine takes
    /// them out of the starting balances when the same claim is adjudicated
    /// again, and reverses them before writing the new ones. Stores that do
    /// not journal per claim return none (the default).
    /// </summary>
    Task<IReadOnlyList<AccumulatorUpdate>> GetClaimUpdatesAsync(
        string memberId, string subscriberId,
        Guid benefitPlanId, string planYear,
        string claimId,
        CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<AccumulatorUpdate>>([]);
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
    /// the plan year. Resolved at <see cref="IBenefitPlanProvider"/>
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

    // ── Coordination of benefits ──

    /// <summary>
    /// How this plan credits its deductible when it pays secondary or later.
    /// Defaults to <see cref="CobDeductibleCredit.NaicFullCredit"/> (NAIC
    /// MDL-120 §7). See <see cref="CobDeductibleCredit"/>.
    /// </summary>
    public CobDeductibleCredit CobDeductibleCredit { get; init; } = CobDeductibleCredit.NaicFullCredit;

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

    // Benefit categories. Multiple entries may share the same
    // ServiceTypeCode after BP 5.10 — projection no longer
    // deduplicates so the rule gate can pick the correct benefit per
    // member encounter via BenefitRulePredicate evaluation. Use
    // GetCategories(code) for predicate-aware lookup;
    // GetFirstCategory(code) is the legacy any-match shim.
    public List<BenefitCategoryConfig> Categories { get; init; } = [];

    // Cross-reference
    public string? QnxtPlanId { get; init; }

    /// <summary>
    /// Legacy any-match accessor — returns the first
    /// <see cref="BenefitCategoryConfig"/> whose <c>ServiceTypeCode</c>
    /// matches. Kept for callers that don't need predicate evaluation
    /// (e.g. limit checks, audit lookups). For benefit selection during
    /// adjudication go through <see cref="GetCategories"/> + the rule
    /// gate so age/gender/diagnosis predicates are honoured.
    /// </summary>
    public BenefitCategoryConfig? GetFirstCategory(string serviceTypeCode)
        => GetCategories(serviceTypeCode).FirstOrDefault();

    /// <summary>
    /// Returns every <see cref="BenefitCategoryConfig"/> whose
    /// <c>ServiceTypeCode</c> matches, preserving authoring order. Used
    /// by <c>IBenefitRuleGate</c> to walk candidate benefits and pick
    /// the first whose <see cref="BenefitCategoryConfig.Predicate"/>
    /// is satisfied for the current member encounter. See
    /// <see cref="LookupCategories"/> for the match order.
    /// </summary>
    public IReadOnlyList<BenefitCategoryConfig> GetCategories(string serviceTypeCode, string? x12Code = null)
        => LookupCategories(serviceTypeCode, x12Code).Categories;

    /// <summary>
    /// Finds the plan categories for a resolved service category. Match
    /// order, first non-empty wins:
    /// <list type="number">
    ///   <item>The exact name (<paramref name="serviceTypeCode"/>).</item>
    ///   <item>The specific X12 code the resolver used
    ///     (<paramref name="x12Code"/>, <see cref="ServiceCategoryMatch.X12Code"/>):
    ///     a POS 55 line resolves to Behavioral Health via AI (substance
    ///     abuse), so a plan keyed by "AI" takes that category even when it
    ///     also has an "A4" (psychiatric) category.</item>
    ///   <item>The name's other X12 codes, primary first (see
    ///     <see cref="ServiceCategoryNames.X12CodesFor"/>), so plans authored
    ///     with X12 codes keep matching.</item>
    ///   <item>Only when nothing above matched: the rollout fallback category
    ///     (<see cref="ServiceCategoryNames.RolloutFallbackFor"/>, e.g. Urgent
    ///     Care → Office Visit), by its name and then its X12 codes. Reported
    ///     on <see cref="BenefitCategoryLookup.FallbackCategory"/> so the
    ///     caller can log it.</item>
    /// </list>
    /// </summary>
    public BenefitCategoryLookup LookupCategories(string serviceTypeCode, string? x12Code = null)
    {
        var direct = MatchingNameOrCodes(serviceTypeCode, x12Code);
        if (direct.Count > 0) return new BenefitCategoryLookup(direct, null);

        var fallback = ServiceCategoryNames.RolloutFallbackFor(serviceTypeCode);
        if (fallback is not null)
        {
            var viaFallback = MatchingNameOrCodes(fallback, null);
            if (viaFallback.Count > 0) return new BenefitCategoryLookup(viaFallback, fallback);
        }

        return new BenefitCategoryLookup([], null);
    }

    private List<BenefitCategoryConfig> MatchingNameOrCodes(string name, string? specificX12Code)
    {
        var exact = Matching(name);
        if (exact.Count > 0) return exact;

        if (!string.IsNullOrWhiteSpace(specificX12Code))
        {
            var specific = Matching(specificX12Code.Trim());
            if (specific.Count > 0) return specific;
        }

        foreach (var x12 in ServiceCategoryNames.X12CodesFor(name))
        {
            var aliased = Matching(x12);
            if (aliased.Count > 0) return aliased;
        }
        return exact;
    }

    private List<BenefitCategoryConfig> Matching(string serviceTypeCode)
        => Categories
            .Where(c => string.Equals(c.ServiceTypeCode, serviceTypeCode, StringComparison.OrdinalIgnoreCase))
            .ToList();
}

/// <summary>
/// Result of <see cref="BenefitPlanConfig.LookupCategories"/>: the matching
/// categories, and the rollout fallback category name when they were found
/// through <see cref="ServiceCategoryNames.RolloutFallbackFor"/> rather than
/// the resolved category itself (null otherwise).
/// </summary>
public sealed record BenefitCategoryLookup(
    IReadOnlyList<BenefitCategoryConfig> Categories,
    string? FallbackCategory);

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

    /// <summary>
    /// Optional declarative gate (capability BP 5.10) that restricts
    /// when this benefit applies to the member encounter. Carries the
    /// originating <see cref="BenefitRulePredicate"/> the projection
    /// was built from. <c>null</c> means the benefit is unconditionally
    /// applicable for any encounter that resolves to its
    /// <see cref="ServiceTypeCode"/>.
    /// </summary>
    public BenefitRulePredicate? Predicate { get; init; }
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

    /// <summary>
    /// Whether the member cost share this rule produces counts toward the
    /// out-of-pocket maximum. Defaults to true. When false, the amount is
    /// neither added to the OOP accumulators (individual, family, ACA
    /// per-member cap) nor reduced by the OOP max cap — the member owes it
    /// even after the OOP max is met. Deductible accumulation is unaffected.
    /// Typical uses: out-of-network balances, non-EHB services,
    /// grandfathered plan designs. For non-grandfathered plans, in-network
    /// EHB cost sharing must count (45 CFR 156.130); plan validation warns.
    /// </summary>
    public bool OopApplies { get; init; } = true;
}
