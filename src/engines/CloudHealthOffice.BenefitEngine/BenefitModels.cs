namespace CloudHealthOffice.BenefitEngine.Models;

using CloudHealthOffice.BenefitEngine.Domain;

// ═══════════════════════════════════════════════════════════════════
// REQUEST: What the adjudication workflow sends to the engine
// ═══════════════════════════════════════════════════════════════════

/// <summary>
/// A request to resolve benefits for one or more claim lines.
/// Sent by the Argo adjudication workflow step "get-benefits" + "calculate-payment".
///
/// The engine receives the full claim context so it can make decisions
/// that depend on cross-line interactions (e.g., multiple procedure reductions,
/// E/M with procedure on same date, etc.).
/// </summary>
public record BenefitResolutionRequest
{
    /// <summary>
    /// Member identifier (QNXT member ID or CHO member ID).
    /// </summary>
    public string MemberId { get; init; } = default!;

    /// <summary>
    /// Subscriber identifier (for family accumulator lookups).
    /// </summary>
    public string SubscriberId { get; init; } = default!;

    /// <summary>
    /// The benefit plan the member is enrolled in.
    /// </summary>
    public Guid BenefitPlanId { get; init; }

    /// <summary>
    /// Date of service (for accumulator period determination).
    /// </summary>
    public DateOnly ServiceDate { get; init; }

    /// <summary>
    /// Network status of the rendering provider for this claim.
    /// Determined by the "validate provider" step before this one.
    /// </summary>
    public NetworkTier NetworkTier { get; init; }

    /// <summary>
    /// The claim lines to resolve benefits for.
    /// </summary>
    public List<ClaimLineInput> Lines { get; init; } = [];

    /// <summary>
    /// Allowed amounts per line (from the pricing/fee schedule step).
    /// Keyed by line number. If not provided, engine uses billed charges.
    /// </summary>
    public Dictionary<int, decimal> AllowedAmounts { get; init; } = [];

    /// <summary>
    /// Optional: claim-level context used by some benefit rules.
    /// </summary>
    public string? ClaimType { get; init; } // 837P, 837I, 837D
    public string? AdmitDate { get; init; }
    public string? DischargeDate { get; init; }
    public string? DrgCode { get; init; }
    public bool IsEmergency { get; init; }
}

/// <summary>
/// A single claim line from the inbound claim.
/// </summary>
public record ClaimLineInput
{
    public int LineNumber { get; init; }
    public string ProcedureCode { get; init; } = default!;
    public string? CodeType { get; init; } = "CPT"; // CPT, HCPCS, CDT, Revenue
    public List<string> Modifiers { get; init; } = [];
    public string? RevenueCode { get; init; }
    public string PlaceOfService { get; init; } = default!;
    public decimal BilledAmount { get; init; }
    public decimal Units { get; init; } = 1;
    public List<string> DiagnosisCodes { get; init; } = [];
}

// ═══════════════════════════════════════════════════════════════════
// RESPONSE: What the engine returns to the adjudication workflow
// ═══════════════════════════════════════════════════════════════════

/// <summary>
/// Complete benefit resolution result for a claim.
/// This is what the "calculate-payment" step uses to update the claim
/// with final adjudication amounts.
/// </summary>
public record BenefitResolutionResult
{
    /// <summary>
    /// Overall success/failure of the resolution.
    /// </summary>
    public bool Success { get; init; }

    /// <summary>
    /// If the entire claim is denied at the benefit level (e.g., service not covered),
    /// this contains the denial reason.
    /// </summary>
    public string? DenialReasonCode { get; init; } // CARC code
    public string? DenialReasonDescription { get; init; }

    /// <summary>
    /// Per-line benefit determination results.
    /// </summary>
    public List<LineBenefitResult> Lines { get; init; } = [];

    /// <summary>
    /// Claim-level totals (sum of lines).
    /// </summary>
    public ClaimTotals Totals { get; init; } = new();

    /// <summary>
    /// Accumulator state after applying this claim.
    /// Useful for the portal / 271 responses.
    /// </summary>
    public List<AccumulatorState> AccumulatorSnapshot { get; init; } = [];
}

/// <summary>
/// Benefit determination for a single claim line.
/// Contains everything needed to populate the adjudication result
/// and eventually generate CAS segments in the 835.
/// </summary>
public record LineBenefitResult
{
    public int LineNumber { get; init; }

    /// <summary>
    /// Is this service covered under the member's plan?
    /// </summary>
    public bool IsCovered { get; init; }

    /// <summary>
    /// The benefit category this line was mapped to.
    /// </summary>
    public string ServiceTypeCode { get; init; } = default!;
    public string ServiceTypeDescription { get; init; } = default!;

    /// <summary>
    /// Was prior authorization required? Was it found?
    /// (Populated by a preceding workflow step; echoed here for completeness.)
    /// </summary>
    public bool AuthRequired { get; init; }
    public bool AuthFound { get; init; }

    // ── Financial Breakdown ──

    /// <summary>
    /// What the provider billed.
    /// </summary>
    public decimal BilledAmount { get; init; }

    /// <summary>
    /// What the fee schedule / contract allows (from pricing step).
    /// </summary>
    public decimal AllowedAmount { get; init; }

    /// <summary>
    /// Billed minus Allowed. Adjusted under CARC 45 (Charges exceed fee schedule).
    /// </summary>
    public decimal ContractualAdjustment { get; init; }

    /// <summary>
    /// Amount applied to deductible (member responsibility).
    /// </summary>
    public decimal DeductibleAmount { get; init; }

    /// <summary>
    /// Copay amount (member responsibility).
    /// </summary>
    public decimal CopayAmount { get; init; }

    /// <summary>
    /// Coinsurance amount (member responsibility).
    /// Calculated as: (AllowedAmount - DeductibleAmount) × CoinsurancePercent
    /// </summary>
    public decimal CoinsuranceAmount { get; init; }

    /// <summary>
    /// The coinsurance percentage applied (for transparency/audit).
    /// </summary>
    public decimal CoinsurancePercent { get; init; }

    /// <summary>
    /// Amount reduced because OOP max was reached.
    /// When OOP max is hit, remaining member responsibility is waived.
    /// </summary>
    public decimal OopMaxReduction { get; init; }

    /// <summary>
    /// Total member responsibility = Deductible + Copay + Coinsurance - OopMaxReduction
    /// </summary>
    public decimal MemberResponsibility { get; init; }

    /// <summary>
    /// What the plan pays = AllowedAmount - MemberResponsibility
    /// </summary>
    public decimal PlanPaidAmount { get; init; }

    /// <summary>
    /// Adjustment reason codes for 835 CAS segment generation.
    /// </summary>
    public List<AdjustmentReason> Adjustments { get; init; } = [];

    /// <summary>
    /// If the line is denied, why.
    /// </summary>
    public string? DenialReasonCode { get; init; }
    public string? DenialReasonDescription { get; init; }
}

/// <summary>
/// Claim Adjustment Reason — maps directly to CAS segments in the 835.
/// Group code + CARC + optional RARC + amount.
/// </summary>
public record AdjustmentReason
{
    /// <summary>
    /// CAS group code:
    /// CO = Contractual Obligation (provider write-off)
    /// PR = Patient Responsibility
    /// OA = Other Adjustment
    /// PI = Payer Initiated Reductions
    /// CR = Corrections/Reversals
    /// </summary>
    public string GroupCode { get; init; } = default!;

    /// <summary>
    /// CARC — Claim Adjustment Reason Code.
    /// Examples: 1 (Deductible), 2 (Coinsurance), 3 (Copay),
    /// 45 (Charges exceed fee schedule), 96 (Non-covered charge),
    /// 197 (Auth/pre-cert required), etc.
    /// </summary>
    public string ReasonCode { get; init; } = default!;

    /// <summary>
    /// RARC — Remittance Advice Remark Code (optional, supplemental).
    /// Examples: N30 (Missing auth), M76 (Missing/incomplete records).
    /// </summary>
    public string? RemarkCode { get; init; }

    /// <summary>
    /// Adjustment amount.
    /// </summary>
    public decimal Amount { get; init; }
}

/// <summary>
/// Claim-level totals — sums across all lines.
/// </summary>
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

/// <summary>
/// Accumulator state after applying a claim.
/// </summary>
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
