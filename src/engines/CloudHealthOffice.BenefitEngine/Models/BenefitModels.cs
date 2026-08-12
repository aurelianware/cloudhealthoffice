namespace CloudHealthOffice.BenefitEngine.Models;

using System.Text.Json.Serialization;
using CloudHealthOffice.BenefitEngine.Domain;

// ═══════════════════════════════════════════════════════════════════
// REQUEST
// ═══════════════════════════════════════════════════════════════════

public record BenefitResolutionRequest
{
    public string MemberId { get; init; } = default!;
    public string SubscriberId { get; init; } = default!;
    public Guid BenefitPlanId { get; init; }
    public DateOnly ServiceDate { get; init; }
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public NetworkTier NetworkTier { get; init; }
    public List<ClaimLineInput> Lines { get; init; } = [];
    public Dictionary<int, decimal> AllowedAmounts { get; init; } = [];
    public string ClaimId { get; init; } = default!;

    // Claim-level context
    /// <summary>
    /// Line of business from coverage (1=Commercial, 2=Medicare, 3=Medicaid, etc.).
    /// Available for LOB-specific adjudication rules in future iterations.
    /// </summary>
    public int? LineOfBusiness { get; init; }

    public string? ClaimType { get; init; } // 837P, 837I, 837D
    public string? AdmitDate { get; init; }
    public string? DischargeDate { get; init; }
    public bool IsEmergency { get; init; }

    // ── DRG / Inpatient ──

    /// <summary>
    /// DRG code assigned to this inpatient stay (e.g., "470" for hip replacement).
    /// When present and the benefit category uses DrgCaseRate pricing,
    /// the engine applies cost-sharing once per admission using the
    /// DRG allowed amount rather than per-line.
    /// </summary>
    public string? DrgCode { get; init; }

    /// <summary>
    /// DRG case rate allowed amount from the FeeScheduleEngine.
    /// When InpatientPricingMethod is DrgCaseRate, this is the total
    /// allowed amount for the entire stay. Individual line allowed
    /// amounts are ignored for cost-sharing purposes (though they're
    /// still tracked for reporting).
    /// </summary>
    public decimal? DrgAllowedAmount { get; init; }

    /// <summary>
    /// Length of stay in days (for per-diem pricing).
    /// </summary>
    public int? LengthOfStay { get; init; }

    /// <summary>
    /// COB context. Null for primary claims.
    /// </summary>
    public CobInfo? Cob { get; init; }

    /// <summary>
    /// Member demographics + diagnosis context fed into
    /// <see cref="Domain.BenefitRulePredicate"/> evaluation during the
    /// adjudication hot path (capability BP 5.10). Optional. When null,
    /// the engine skips predicate evaluation entirely and treats every
    /// candidate benefit as applicable — see Decision 3 in
    /// <c>docs/architecture/adjudication-api-stabilization.md</c>.
    /// </summary>
    public MemberContext? Member { get; init; }

    /// <summary>
    /// Execution context for this calculation. Defaults to
    /// <see cref="AdjudicationExecutionMode.Production"/> so real claim
    /// adjudication persists accumulator updates exactly as before. Set to
    /// <see cref="AdjudicationExecutionMode.Prospective"/> for a read-only
    /// payment estimate: the same cost-sharing waterfall runs but the engine
    /// skips the accumulator write, leaving all persistent financial state
    /// untouched. See <c>docs/architecture/prospective-adjudication.md</c>.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AdjudicationExecutionMode ExecutionMode { get; init; }
        = AdjudicationExecutionMode.Production;
}

/// <summary>
/// Optional member-and-encounter context supplied by the caller for
/// declarative benefit-rule evaluation. Populated from coverage
/// information / member demographics at the controller seam. When the
/// caller can't supply a field it is left null and the predicate
/// either ignores the missing facet (no opinion) or fails closed
/// (context-required facets) — see <see cref="Domain.BenefitRulePredicate.Evaluate"/>.
/// </summary>
public record MemberContext
{
    public int? AgeYears { get; init; }
    public BenefitMemberGender? Gender { get; init; }
    public IReadOnlyCollection<string>? DiagnosisCodes { get; init; }
}

public record CobInfo
{
    public int PayerSequence { get; init; } = 1;
    public bool UseComplementaryModel { get; init; } = true;
    public string? PrimaryPayerId { get; init; }
    public string? PrimaryPayerName { get; init; }
    public Dictionary<int, decimal> PrimaryPayerPaymentByLine { get; init; } = [];
    public Dictionary<int, decimal> PrimaryAllowedByLine { get; init; } = [];
}

public record ClaimLineInput
{
    public int LineNumber { get; init; }
    public string ProcedureCode { get; init; } = default!;
    public string? CodeType { get; init; } = "CPT";
    public List<string> Modifiers { get; init; } = [];
    public string? RevenueCode { get; init; }
    public string PlaceOfService { get; init; } = default!;
    public decimal BilledAmount { get; init; }
    public decimal Units { get; init; } = 1;
    public List<string> DiagnosisCodes { get; init; } = [];
}

// ═══════════════════════════════════════════════════════════════════
// RESPONSE
// ═══════════════════════════════════════════════════════════════════

public record BenefitResolutionResult
{
    public bool Success { get; init; }
    public string? DenialReasonCode { get; init; }
    public string? DenialReasonDescription { get; init; }
    public List<LineBenefitResult> Lines { get; init; } = [];
    public ClaimTotals Totals { get; init; } = new();
    public List<AccumulatorState> AccumulatorSnapshot { get; init; } = [];
    public IReadOnlyDictionary<string, double> Timings { get; init; } = new Dictionary<string, double>();

    /// <summary>
    /// When DRG/per-diem pricing is used, this contains the claim-level
    /// cost-sharing breakdown (since cost-sharing is per-admission, not per-line).
    /// </summary>
    public DrgCostShareResult? DrgCostShare { get; init; }
}

/// <summary>
/// Claim-level cost-sharing for DRG/per-diem inpatient admissions.
/// </summary>
public record DrgCostShareResult
{
    public string? DrgCode { get; init; }
    public decimal DrgAllowedAmount { get; init; }
    public decimal DeductibleAmount { get; init; }
    public decimal CopayAmount { get; init; }
    public decimal CoinsuranceAmount { get; init; }
    public decimal CoinsurancePercent { get; init; }
    public decimal OopMaxReduction { get; init; }
    public decimal MemberResponsibility { get; init; }
    public decimal PlanPaidAmount { get; init; }
    public List<AdjustmentReason> Adjustments { get; init; } = [];
}

public record LineBenefitResult
{
    public int LineNumber { get; init; }
    public bool IsCovered { get; init; }
    public string ServiceTypeCode { get; init; } = default!;
    public string ServiceTypeDescription { get; init; } = default!;
    public bool AuthRequired { get; init; }
    public bool AuthFound { get; init; }

    // Financial breakdown
    public decimal BilledAmount { get; init; }
    public decimal AllowedAmount { get; init; }
    public decimal ContractualAdjustment { get; init; }
    public decimal DeductibleAmount { get; init; }
    public decimal CopayAmount { get; init; }
    public decimal CoinsuranceAmount { get; init; }
    public decimal CoinsurancePercent { get; init; }
    public decimal OopMaxReduction { get; init; }
    public decimal MemberResponsibility { get; init; }
    public decimal PlanPaidAmount { get; init; }
    public List<AdjustmentReason> Adjustments { get; init; } = [];
    public string? DenialReasonCode { get; init; }
    public string? DenialReasonDescription { get; init; }

    /// <summary>
    /// True when this line's cost-sharing was calculated at the claim level
    /// (DRG/per-diem) rather than per-line. In this case, the line-level
    /// amounts are allocated shares of the claim-level cost-sharing.
    /// </summary>
    public bool IsDrgPriced { get; init; }
}

public record AdjustmentReason
{
    public string GroupCode { get; init; } = default!;
    public string ReasonCode { get; init; } = default!;
    public string? RemarkCode { get; init; }
    public decimal Amount { get; init; }
}

public record ClaimTotals
{
    public decimal TotalBilled { get; init; }
    public decimal TotalAllowed { get; init; }
    public decimal TotalContractualAdjustment { get; init; }
    public decimal TotalDeductible { get; init; }
    public decimal TotalCopay { get; init; }
    public decimal TotalCoinsurance { get; init; }
    public decimal TotalOopMaxReduction { get; init; }
    public decimal TotalMemberResponsibility { get; init; }
    public decimal TotalPlanPaid { get; init; }
}

public record AccumulatorState
{
    public AccumulatorType Type { get; init; }
    public AccumulatorScope Scope { get; init; }
    public NetworkTier NetworkTier { get; init; }
    public decimal LimitAmount { get; init; }
    public decimal AccumulatedAmountBefore { get; init; }
    public decimal AmountApplied { get; init; }
    public decimal AccumulatedAmountAfter { get; init; }
    public decimal RemainingAmount { get; init; }
    public bool LimitReached { get; init; }
}
