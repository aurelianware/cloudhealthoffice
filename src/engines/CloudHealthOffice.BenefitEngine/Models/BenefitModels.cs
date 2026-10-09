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

    /// <summary>
    /// The claim this one replaces (a corrected version — claims-service
    /// <c>PredecessorVersionId</c>), if any. Its accumulator updates are kept
    /// out of the starting balances (the replacement must not meet a
    /// deductible its predecessor met) and reversed when this claim's
    /// updates are written.
    /// </summary>
    public string? ReplacesClaimId { get; init; }

    // Claim-level context
    /// <summary>
    /// Line of business from coverage (1=Commercial, 2=Medicare, 3=Medicaid, etc.).
    /// Available for LOB-specific adjudication rules in future iterations.
    /// </summary>
    public int? LineOfBusiness { get; init; }

    public string? ClaimType { get; init; } // 837P, 837I, 837D

    /// <summary>
    /// NUBC type of bill for an institutional claim (facility type CLM05-1 +
    /// frequency CLM05-3, e.g. "111"). Used by the service category resolver's
    /// fallback when no mapping matches: on an 837I the line's place of service
    /// carries the facility type code, not a CMS place-of-service code.
    /// </summary>
    public string? TypeOfBill { get; init; }

    /// <summary>
    /// True when the lines' <see cref="ClaimLineInput.PlaceOfService"/> holds
    /// CLM05-1, the institutional facility type code, rather than a CMS place
    /// of service — set by the claims-service 837I mapping. The service
    /// category resolver then never reads it as a place of service. False
    /// (the default) means it is a real CMS place of service.
    /// </summary>
    public bool PlaceOfServiceIsFacilityType { get; init; }
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
    /// How the claim was actually priced, when the caller knows it: set to
    /// <see cref="InpatientPricingMethod.DrgCaseRate"/> or
    /// <see cref="InpatientPricingMethod.PerDiem"/> when the fee schedule paid
    /// the stay as one claim-level amount (a DRG case rate or an all-inclusive
    /// per diem) and <see cref="DrgAllowedAmount"/> carries that amount. It
    /// overrides the plan's <c>DefaultInpatientPricingMethod</c> for 837I
    /// claims, so cost sharing is applied once per stay (one inpatient copay,
    /// deductible once) rather than per line. Null = the plan default, as before.
    /// </summary>
    public InpatientPricingMethod? InpatientPricingMethod { get; init; }

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

/// <summary>
/// COB context for a claim on which this plan is not the first payer.
/// </summary>
public record CobInfo
{
    /// <summary>
    /// This plan's payer responsibility sequence (837 2000B SBR01: P = 1,
    /// S = 2, T = 3, A–H = 4–11). COB is applied when it is 2 or more.
    /// </summary>
    public int PayerSequence { get; init; } = 1;

    /// <summary>true = standard / complementary COB (NAIC MDL-120 §7), false = non-duplication.</summary>
    public bool UseComplementaryModel { get; init; } = true;
    public string? PrimaryPayerId { get; init; }
    public string? PrimaryPayerName { get; init; }

    /// <summary>
    /// Legacy secondary-only input: the primary payer's payment by line.
    /// Used only when <see cref="PriorPayers"/> is empty.
    /// </summary>
    public Dictionary<int, decimal> PrimaryPayerPaymentByLine { get; init; } = [];
    public Dictionary<int, decimal> PrimaryAllowedByLine { get; init; } = [];

    /// <summary>
    /// Every payer that adjudicated before this plan (837 loops 2320/2330B
    /// at claim level, 2430 at line level), with paid amounts and CAS
    /// adjustments. Payers whose sequence is not below
    /// <see cref="PayerSequence"/> are ignored. See
    /// <see cref="CloudHealthOffice.CobEngine.Services.PriorPayerAllocator"/>
    /// for how claim- and line-level amounts become per-line amounts.
    /// </summary>
    public List<CloudHealthOffice.CobEngine.Domain.PriorPayerAdjudication> PriorPayers { get; init; } = [];
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

    /// <summary>
    /// True when the engine could not adjudicate the claim as submitted and it
    /// must be pended for manual review rather than denied — e.g. a per-stay
    /// (DRG / all-inclusive per-diem) allocation that pays a line more than it
    /// billed, which no balanced remittance can represent. <see cref="Success"/>
    /// is false, no cost share is computed and no accumulators are written.
    /// </summary>
    public bool RequiresReview { get; init; }

    /// <summary>Pend code when <see cref="RequiresReview"/> is true (e.g. "PRICING").</summary>
    public string? PendReasonCode { get; init; }

    /// <summary>Why the claim needs review, when <see cref="RequiresReview"/> is true.</summary>
    public string? PendReason { get; init; }

    public List<LineBenefitResult> Lines { get; init; } = [];
    public ClaimTotals Totals { get; init; } = new();
    public List<AccumulatorState> AccumulatorSnapshot { get; init; } = [];
    public IReadOnlyDictionary<string, double> Timings { get; init; } = new Dictionary<string, double>();

    /// <summary>
    /// When DRG/per-diem pricing is used, this contains the claim-level
    /// cost-sharing breakdown (since cost-sharing is per-admission, not per-line).
    /// </summary>
    public DrgCostShareResult? DrgCostShare { get; init; }

    /// <summary>
    /// The payer sequence this plan adjudicated the claim in when
    /// coordination of benefits was applied (2 secondary, 3 tertiary, …);
    /// null when it adjudicated as the first payer. Drives the 835 CLP02.
    /// </summary>
    public int? CobPayerSequence { get; init; }
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

    /// <summary>Deductible credited to the accumulators for the stay; see
    /// <see cref="LineBenefitResult.DeductibleCreditedAmount"/>.</summary>
    public decimal DeductibleCreditedAmount { get; init; }
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

    /// <summary>
    /// Informational: cost share the out-of-pocket maximum forgave on this
    /// line. Already netted out of <see cref="DeductibleAmount"/>,
    /// <see cref="CopayAmount"/> and <see cref="CoinsuranceAmount"/> (and
    /// their PR-1/PR-3/PR-2 adjustments), which carry only what the member
    /// owes; it is not an adjustment and must not be subtracted again.
    /// </summary>
    public decimal OopMaxReduction { get; init; }
    public decimal MemberResponsibility { get; init; }

    /// <summary>
    /// Portion of <see cref="MemberResponsibility"/> that counts toward the
    /// out-of-pocket maximum (what the engine added to the OOP accumulators).
    /// Lower than <see cref="MemberResponsibility"/> when a cost-share rule
    /// has <see cref="Services.CostShareRuleConfig.OopApplies"/> = false.
    /// Downstream OOP accumulation (ClaimFinalizedEvent.OopApplied) must use
    /// this, not member responsibility.
    /// </summary>
    public decimal OopAppliedAmount { get; init; }

    /// <summary>
    /// Deductible credited to the deductible accumulators for this line.
    /// Equals <see cref="DeductibleAmount"/> (the PR-1 the member owes)
    /// except when this plan is secondary or later and the plan's
    /// <see cref="CobDeductibleCredit"/> is
    /// <see cref="CobDeductibleCredit.NaicFullCredit"/>: then it is the
    /// deductible this plan would have applied with no other coverage
    /// (NAIC MDL-120 §7), including deductible a prior payer covered.
    /// Downstream deductible accumulation (ClaimFinalizedEvent
    /// DeductibleCredited) must use this; the 835 uses
    /// <see cref="DeductibleAmount"/>.
    /// </summary>
    public decimal DeductibleCreditedAmount { get; init; }

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

    /// <summary>Sum of <see cref="LineBenefitResult.OopAppliedAmount"/>.</summary>
    public decimal TotalOopApplied { get; init; }

    /// <summary>Sum of <see cref="LineBenefitResult.DeductibleCreditedAmount"/>.</summary>
    public decimal TotalDeductibleCredited { get; init; }

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
