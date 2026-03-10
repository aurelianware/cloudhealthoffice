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

    /// <summary>Amount this secondary payer allowed (from its own fee schedule).</summary>
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

    /// <summary>Secondary plan payment after COB adjustment.</summary>
    public decimal SecondaryPlanPayment { get; init; }

    /// <summary>Final member responsibility after both payers have applied.</summary>
    public decimal MemberResponsibility { get; init; }

    /// <summary>
    /// Amount by which the secondary plan payment was reduced due to COB
    /// (reported as OA/23 CAS segment on the 835).
    /// </summary>
    public decimal CobReduction { get; init; }

    /// <summary>True if COB logic changed any amounts; false if COB was a no-op.</summary>
    public bool CobApplied { get; init; }
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
