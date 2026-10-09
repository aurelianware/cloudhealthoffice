namespace CloudHealthOffice.CobEngine.Domain;

// ═══════════════════════════════════════════════════════════════════
// ENUMS
// ═══════════════════════════════════════════════════════════════════

/// <summary>
/// Payer sequence for this claim submission.
/// Mirrors X12 SBR01 / COB segment payer responsibility codes.
/// </summary>
public enum PayerSequenceCode
{
    /// <summary>This payer is the primary (no other payer paid first).</summary>
    Primary = 1,

    /// <summary>This payer is secondary (primary payer has already adjudicated).</summary>
    Secondary = 2,

    /// <summary>This payer is tertiary (both primary and secondary have adjudicated).</summary>
    Tertiary = 3
}

/// <summary>
/// COB calculation model used by the secondary payer.
///
/// Complementary (most common — commercial): secondary fills the gap between
///   primary payment and total charges, subject to its own benefit limits.
///
/// Non-duplication: secondary only pays if its own benefit would have exceeded
///   the primary's payment. No double-dipping.
/// </summary>
public enum CobModel
{
    Complementary,
    NonDuplication
}

/// <summary>
/// Rule used to determine which plan is primary for a dependent child.
/// </summary>
public enum PayerOrderRule
{
    /// <summary>
    /// Earlier birthday (month/day) in the calendar year → primary plan.
    /// Most common rule for dual-covered dependents.
    /// </summary>
    BirthdayRule,

    /// <summary>
    /// Longer coverage duration → primary (used when birthdays fall on the same day).
    /// </summary>
    LongerDuration,

    /// <summary>
    /// Active employment: the plan from the actively-employed parent is primary.
    /// Used when one parent is retired/COBRA and one is still employed.
    /// </summary>
    ActiveEmployment,

    /// <summary>
    /// Medicare Secondary Payer (MSP): employer coverage is primary when the
    /// patient is an active employee at a large group health plan (≥20 employees).
    /// </summary>
    MedicareSecondaryPayer,

    /// <summary>
    /// Medicare is primary (patient is retired or employer is small group < 20 employees).
    /// </summary>
    MedicarePrimary,

    /// <summary>
    /// Coordination order was explicitly set on the coverage record (no rule needed).
    /// </summary>
    ExplicitCoverageRecord
}

// ═══════════════════════════════════════════════════════════════════
// COB INFORMATION — input from the claim / workflow
// ═══════════════════════════════════════════════════════════════════

/// <summary>
/// COB context carried on a claim when it is submitted as secondary or tertiary.
/// Populated by the claims intake workflow from the 837 OI/MOA/AMT segments
/// or from the coverage-service /cob lookup.
/// </summary>
public record CobInfo
{
    /// <summary>This claim's payer sequence (Primary / Secondary / Tertiary).</summary>
    public PayerSequenceCode PayerSequence { get; init; }

    /// <summary>Calculation model the secondary should apply.</summary>
    public CobModel Model { get; init; } = CobModel.Complementary;

    /// <summary>Payer ID of the primary insurer (for reference / 835 output).</summary>
    public string? PrimaryPayerId { get; init; }

    /// <summary>Payer name of the primary insurer.</summary>
    public string? PrimaryPayerName { get; init; }

    /// <summary>
    /// Amount the primary payer paid per claim line (keyed by line number).
    /// Source: 837 AMT*D segments or prior 835 SVC payment amounts.
    /// </summary>
    public Dictionary<int, decimal> PrimaryPayerPaymentByLine { get; init; } = [];

    /// <summary>
    /// Amount the primary payer allowed per line (for non-duplication model).
    /// Source: 837 AMT*B6 segments or prior 835 SVC allowed amounts.
    /// </summary>
    public Dictionary<int, decimal> PrimaryAllowedByLine { get; init; } = [];
}

// ═══════════════════════════════════════════════════════════════════
// COB CALCULATION — per-line input / output
// ═══════════════════════════════════════════════════════════════════

/// <summary>
/// Input to the COB calculation for a single claim line.
/// Built from the secondary's own adjudication result + primary's payment info.
/// </summary>
public record CobLineInput
{
    public int LineNumber { get; init; }

    /// <summary>Total billed (submitted) charge for this line.</summary>
    public decimal BilledAmount { get; init; }

    /// <summary>Amount this secondary payer allowed (from its own fee schedule).
    /// Required by both models: member responsibility is measured against it.</summary>
    public decimal SecondaryAllowedAmount { get; init; }

    /// <summary>Member responsibility produced by the secondary's own cost-sharing waterfall,
    /// before any COB reduction is applied.</summary>
    public decimal SecondaryMemberResponsibilityBeforeCob { get; init; }

    /// <summary>Plan payment produced by the secondary's own waterfall, before COB.</summary>
    public decimal SecondaryPlanPaymentBeforeCob { get; init; }

    /// <summary>Amount the primary payer actually paid for this line.</summary>
    public decimal PrimaryPayerPayment { get; init; }

    /// <summary>Amount the primary payer allowed (used by non-duplication model).</summary>
    public decimal PrimaryAllowedAmount { get; init; }

    /// <summary>Which COB calculation model to apply.</summary>
    public CobModel Model { get; init; }

    /// <summary>
    /// Every payer that adjudicated before this plan, for this unit (one
    /// line, or a whole DRG stay). When this plan is secondary there is one
    /// entry; when tertiary, two; and so on. When empty,
    /// <see cref="PrimaryPayerPayment"/> is taken as the only prior payer
    /// (its patient responsibility unknown) — the secondary-only input used
    /// before tertiary support. Build line amounts from 837 2320/2430 data
    /// with <see cref="Services.PriorPayerAllocator"/>.
    /// </summary>
    public IReadOnlyList<PriorPayerAmount> PriorPayers { get; init; } = [];

    /// <summary>
    /// The prior payers the calculation uses: <see cref="PriorPayers"/>
    /// ordered by sequence, or the legacy single primary payment.
    /// </summary>
    public IReadOnlyList<PriorPayerAmount> EffectivePriorPayers =>
        PriorPayers.Count > 0
            ? PriorPayers.OrderBy(p => p.Sequence).ToList()
            : [new PriorPayerAmount { Sequence = 1, PaidAmount = PrimaryPayerPayment }];
}

/// <summary>
/// One prior payer's adjudication of one COB unit (a line, or a whole DRG
/// stay): what it paid and what it left the member owing.
/// </summary>
public record PriorPayerAmount
{
    /// <summary>Payer responsibility sequence: 1 = primary, 2 = secondary,
    /// 3 = tertiary, 4–11 = payer responsibility four to eleven (SBR01 A–H).</summary>
    public int Sequence { get; init; }

    /// <summary>What this payer paid for the unit (837 2430 SVD02, or its
    /// share of 2320 AMT*D).</summary>
    public decimal PaidAmount { get; init; }

    /// <summary>
    /// What this payer left the member owing for the unit: the sum of its
    /// PR-group CAS (837 2430 CAS, or its share of 2320 CAS). Null when the
    /// claim reports none of the payer's adjustments.
    /// </summary>
    public decimal? PatientResponsibility { get; init; }
}

/// <summary>
/// One CAS adjustment a prior payer reported (837 2320 or 2430 CAS:
/// group code, CARC, amount).
/// </summary>
public record PriorPayerAdjustment
{
    public string GroupCode { get; init; } = string.Empty;
    public string ReasonCode { get; init; } = string.Empty;
    public decimal Amount { get; init; }
}

/// <summary>
/// A prior payer's line-level adjudication (837 loop 2430): SVD02 paid
/// amount and the line's CAS adjustments.
/// </summary>
public record PriorPayerLineAdjudication
{
    /// <summary>The claim line (LX) this 2430 loop belongs to.</summary>
    public int LineNumber { get; init; }

    /// <summary>SVD02 — the amount this payer paid for the line.</summary>
    public decimal PaidAmount { get; init; }

    /// <summary>2430 CAS — this payer's adjustments to the line.</summary>
    public List<PriorPayerAdjustment> Adjustments { get; init; } = [];
}

/// <summary>
/// Everything one prior payer reported on the claim (837 loops 2320 / 2330B
/// at claim level and 2430 at line level), as the COB input carries it.
/// <see cref="Services.PriorPayerAllocator"/> turns it into per-line (or
/// per-stay) <see cref="PriorPayerAmount"/>s.
/// </summary>
public record PriorPayerAdjudication
{
    /// <summary>Payer responsibility sequence from 2320 SBR01 (see
    /// <see cref="PayerResponsibility.ToSequence"/>).</summary>
    public int Sequence { get; init; }

    /// <summary>2330B NM109 — the other payer's identifier.</summary>
    public string? PayerId { get; init; }

    /// <summary>2330B NM103 — the other payer's name.</summary>
    public string? PayerName { get; init; }

    /// <summary>2320 AMT*D — payer paid amount for the claim. Null when not
    /// reported (the 2430 SVD02 amounts are then the claim total).</summary>
    public decimal? ClaimPaidAmount { get; init; }

    /// <summary>2320 CAS — claim-level adjustments (those not reported on a line).</summary>
    public List<PriorPayerAdjustment> ClaimAdjustments { get; init; } = [];

    /// <summary>2430 — line-level adjudication, one entry per line the payer reported.</summary>
    public List<PriorPayerLineAdjudication> Lines { get; init; } = [];
}

/// <summary>
/// X12 SBR01 payer responsibility sequence number codes (005010 837
/// loops 2000B and 2320).
/// </summary>
public static class PayerResponsibility
{
    /// <summary>
    /// Maps SBR01 to a sequence number: P → 1, S → 2, T → 3, A–H → 4–11
    /// (payer responsibility four through eleven). U (unknown), blank or
    /// any other value → null.
    /// </summary>
    public static int? ToSequence(string? sbr01) => sbr01?.Trim().ToUpperInvariant() switch
    {
        "P" => 1,
        "S" => 2,
        "T" => 3,
        "A" => 4,
        "B" => 5,
        "C" => 6,
        "D" => 7,
        "E" => 8,
        "F" => 9,
        "G" => 10,
        "H" => 11,
        _ => null,
    };
}

/// <summary>
/// COB-adjusted amounts for a single claim line.
/// These replace the secondary's pre-COB amounts in the final adjudication result.
/// </summary>
public record CobLineResult
{
    public int LineNumber { get; init; }

    /// <summary>Primary payer payment (carried through for 835/EOB reporting).</summary>
    public decimal PrimaryPayerPayment { get; init; }

    /// <summary>What all prior payers paid together for this unit.</summary>
    public decimal TotalPriorPaid { get; init; }

    /// <summary>Secondary plan payment after COB adjustment.</summary>
    public decimal SecondaryPlanPayment { get; init; }

    /// <summary>
    /// Final member responsibility after both payers have applied: the part
    /// of this plan's allowed amount neither payer paid, never more than
    /// <see cref="CobLineInput.SecondaryMemberResponsibilityBeforeCob"/>. The
    /// caller reduces its PR-1/2/3 entries to this amount.
    /// </summary>
    public decimal MemberResponsibility { get; init; }

    /// <summary>
    /// Amount by which the secondary plan payment was reduced due to COB
    /// (this plan's COB savings). The 835 OA-23 adds the cost share the
    /// member no longer owes to this amount — see
    /// <see cref="Services.CobCalculationService"/>.
    /// </summary>
    public decimal CobReduction { get; init; }

    /// <summary>
    /// True if COB logic changed any amount — the plan payment
    /// (<see cref="CobReduction"/> ≠ 0) or the member responsibility (e.g.
    /// the primary covered the cost share while this plan still pays its
    /// full benefit); false if COB was a no-op.
    /// </summary>
    public bool CobApplied { get; init; }
}

// ═══════════════════════════════════════════════════════════════════
// COB CALCULATION — claim level (NAIC MDL-120 §7 "for that claim")
// ═══════════════════════════════════════════════════════════════════

/// <summary>
/// One COB unit of a claim as this plan adjudicated it before COB, had it
/// been the only plan: a service line (per-line pricing), or the whole stay
/// (DRG / per-diem pricing, one unit).
/// </summary>
public record CobClaimUnit
{
    /// <summary>The claim line number (any number for a single stay unit).</summary>
    public int LineNumber { get; init; }

    /// <summary>Billed charge: the weight claim-level prior payments are prorated by.</summary>
    public decimal BilledAmount { get; init; }

    /// <summary>This plan's allowed amount (the allowable expense, NAIC §3.A).</summary>
    public decimal AllowedAmount { get; init; }

    /// <summary>
    /// This plan's member cost share for the unit before COB (deductible +
    /// copay + coinsurance, after the OOP cap). Normal benefit = allowed −
    /// this.
    /// </summary>
    public decimal CostShareBeforeCob { get; init; }

    /// <summary>The benefit this plan would have paid as the only plan.</summary>
    public decimal NormalBenefit => Math.Max(0, AllowedAmount - Math.Max(0, CostShareBeforeCob));
}

/// <summary>Input to <see cref="Services.ICobCalculationService.CalculateClaim"/>.</summary>
public record CobClaimInput
{
    /// <summary>This plan's units, in line order.</summary>
    public IReadOnlyList<CobClaimUnit> Units { get; init; } = [];

    /// <summary>
    /// Every line on the claim with its charge, for allocating the prior
    /// payers' claim-level amounts (lines this plan denied included, so they
    /// keep their share). Empty = the units.
    /// </summary>
    public IReadOnlyList<Services.PriorPayerAllocator.ClaimLineCharge> ClaimLineCharges { get; init; } = [];

    /// <summary>
    /// The other payers on the claim (837 2320/2330B/2430). Only payers
    /// sequenced before <see cref="OurSequence"/> are used.
    /// </summary>
    public IReadOnlyList<PriorPayerAdjudication> PriorPayers { get; init; } = [];

    /// <summary>This plan's payer sequence (2 secondary, 3 tertiary, …).</summary>
    public int OurSequence { get; init; } = 2;

    public CobModel Model { get; init; } = CobModel.Complementary;

    /// <summary>
    /// When true the units are one DRG / per-diem stay: every prior payer's
    /// amounts are its claim totals (= AMT*D), not line allocations.
    /// </summary>
    public bool SingleStay { get; init; }
}

/// <summary>Result of the claim-level COB calculation.</summary>
public record CobClaimResult
{
    /// <summary>One entry per unit: this plan's payment and the member's
    /// share after COB (<see cref="CobLineResult.SecondaryPlanPayment"/>,
    /// <see cref="CobLineResult.MemberResponsibility"/>).</summary>
    public IReadOnlyList<CobLineResult> Units { get; init; } = [];

    /// <summary>What every prior payer paid on the claim, together.</summary>
    public decimal TotalPriorPaid { get; init; }

    /// <summary>
    /// The allowable expense left for this plan and the member after the
    /// prior payers: Σ per bounding-payer group of min(allowed − prior paid,
    /// that payer's patient responsibility) — see
    /// <see cref="Services.CobCalculationService.CalculateClaim"/>.
    /// </summary>
    public decimal Balance { get; init; }

    public decimal PlanPayment { get; init; }
    public decimal MemberResponsibility { get; init; }
}

// ═══════════════════════════════════════════════════════════════════
// PAYER ORDER DETERMINATION — input / output
// ═══════════════════════════════════════════════════════════════════

/// <summary>
/// Information about one insured party used to determine payer order.
/// Typically represents one parent's coverage for a dual-covered dependent.
/// </summary>
public record InsuredInfo
{
    /// <summary>Internal member / subscriber identifier.</summary>
    public string MemberId { get; init; } = default!;

    /// <summary>Payer / plan identifier for this coverage.</summary>
    public string? PayerId { get; init; }

    /// <summary>
    /// Policyholder's date of birth — used for birthday rule comparison.
    /// Only month and day are compared (year is ignored).
    /// </summary>
    public DateOnly? PolicyholderBirthDate { get; init; }

    /// <summary>Date the coverage became effective — used for longer-duration tiebreaker.</summary>
    public DateOnly? CoverageEffectiveDate { get; init; }

    /// <summary>Whether the policyholder is an active employee (vs. retired / COBRA / dependent).</summary>
    public bool IsActiveEmployee { get; init; }

    /// <summary>Whether this coverage is Medicare.</summary>
    public bool IsMedicare { get; init; }

    /// <summary>
    /// For Medicare: whether Medicare has been designated primary by MSP rules.
    /// Sourced from Coverage.MedicareCoverageInfo.IsPrimaryPayer.
    /// </summary>
    public bool MedicareDesignatedPrimary { get; init; }

    /// <summary>Large group health plan (≥ 20 employees) — affects MSP determination.</summary>
    public bool IsLargeGroupHealthPlan { get; init; }
}

/// <summary>
/// Result of payer order determination for a single coverage.
/// </summary>
public record PayerOrderResult
{
    /// <summary>Determined payer sequence for the coverage described by the input.</summary>
    public PayerSequenceCode PayerSequence { get; init; }

    /// <summary>The rule that drove this determination.</summary>
    public PayerOrderRule Rule { get; init; }

    /// <summary>Human-readable explanation (for audit trail / portal display).</summary>
    public string Explanation { get; init; } = default!;
}
