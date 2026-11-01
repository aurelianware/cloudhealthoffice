namespace CloudHealthOffice.FeeScheduleEngine.Medicaid;

// ═══════════════════════════════════════════════════════════════════
// MEDICAID APR-DRG PRICING
//
// Texas (1 TAC §355.8052) and California (Medi-Cal State Plan Attachment
// 4.19-A, DRG payment method) both pay an inpatient stay as
//
//     base rate x APR-DRG relative weight (x policy adjustors)
//     adjusted for transfers, plus an outlier, within state caps
//
// but disagree on nearly every detail: who sets the base rate, which
// outliers exist and for whom, how a transfer is paid, whether charges cap
// the payment. Each state therefore gets its own pricer over shared input
// shapes, and every number a state publishes (weights, base rates, CCRs,
// thresholds, adjustors) is data, never a constant in code.
//
// The pricers are pure: no I/O, no clock. Each result carries the steps
// that produced it so an examiner or an appeal can read the arithmetic.
// ═══════════════════════════════════════════════════════════════════

/// <summary>
/// One row of a state's APR-DRG weight table: the payment statistics for a
/// base APR-DRG at one severity of illness.
/// </summary>
public sealed record MedicaidDrgWeight
{
    /// <summary>The "DRG-SOI" key, e.g. "139-3".</summary>
    public required string DrgSoi { get; init; }

    /// <summary>Relative weight (Texas) or hospital-specific relative value (Medi-Cal).</summary>
    public required decimal RelativeWeight { get; init; }

    /// <summary>
    /// Mean length of stay (Texas MLOS) or average length of stay (Medi-Cal
    /// ALOS), in days. Divides the DRG payment into a per diem for transfers
    /// and day outliers.
    /// </summary>
    public decimal? MeanLengthOfStay { get; init; }

    /// <summary>Texas day outlier threshold, in days (§355.8052(g)(3)).</summary>
    public decimal? DayOutlierThreshold { get; init; }

    /// <summary>
    /// Medi-Cal care category (e.g. "Neonate", "Pediatric respiratory",
    /// "Obstetrics"), which selects the policy adjustor.
    /// </summary>
    public string? CareCategory { get; init; }
}

/// <summary>
/// The hospital-specific facts a stay is priced with. Comes from the
/// state's hospital file (Texas SDA notices, the Medi-Cal DRG Hospital
/// Characteristics File) or the plan's contract.
/// </summary>
public sealed record MedicaidDrgHospital
{
    /// <summary>The billing NPI the facts are for.</summary>
    public required string Npi { get; init; }

    /// <summary>
    /// Base rate in dollars: the Texas final SDA (add-ons included), or the
    /// Medi-Cal base price before wage adjustment (statewide, remote rural or
    /// hospital-specific transitional).
    /// </summary>
    public required decimal BaseRate { get; init; }

    /// <summary>
    /// Texas rural hospitals only: the final SDA for labor and delivery stays
    /// (§355.8052(e)(2), the "OB-GYN delivery SDA" column of the rural SDA
    /// file). Used instead of <see cref="BaseRate"/> when the stay groups to
    /// a delivery DRG; null means the hospital has no separate delivery SDA.
    /// </summary>
    public decimal? DeliveryBaseRate { get; init; }

    /// <summary>
    /// Ratio used to estimate cost from charges: the Texas interim rate or the
    /// Medi-Cal interim cost-to-charge ratio. Needed only for outliers.
    /// </summary>
    public decimal? CostToChargeRatio { get; init; }

    /// <summary>Texas hospital class; decides the outlier share (children's 100%, others 90%).</summary>
    public TexasHospitalClass TexasClass { get; init; } = TexasHospitalClass.Urban;

    /// <summary>Medi-Cal wage area index. Null applies no wage adjustment (e.g. out-of-state, 1.00).</summary>
    public decimal? WageIndex { get; init; }

    /// <summary>Medicare labor share for the hospital's wage area, e.g. 0.62. Required with <see cref="WageIndex"/>.</summary>
    public decimal? LaborShare { get; init; }

    /// <summary>
    /// The base rate is a Medi-Cal hospital-specific transitional base price,
    /// which the State Plan says is not wage adjusted.
    /// </summary>
    public bool BaseRateIsTransitional { get; init; }

    /// <summary>Medi-Cal designated NICU hospital (selects the NICU neonate adjustor).</summary>
    public bool IsDesignatedNicu { get; init; }
}

/// <summary>Texas hospital class under §355.8052.</summary>
public enum TexasHospitalClass
{
    /// <summary>Urban hospital (subsection (d)).</summary>
    Urban,

    /// <summary>Rural hospital (subsection (e)).</summary>
    Rural,

    /// <summary>Children's hospital (subsection (c)); outliers paid at 100%.</summary>
    Childrens,
}

/// <summary>One inpatient stay to price.</summary>
public sealed record MedicaidDrgStay
{
    /// <summary>The "DRG-SOI" the stay grouped to.</summary>
    public required string DrgSoi { get; init; }

    /// <summary>Allowed (medically necessary) days.</summary>
    public required int LengthOfStayDays { get; init; }

    /// <summary>Allowed (covered) charges for the stay.</summary>
    public required decimal AllowedCharges { get; init; }

    /// <summary>Age in whole years at admission. Texas outliers are for clients under 21.</summary>
    public int? AgeYearsAtAdmission { get; init; }

    /// <summary>UB-04 patient discharge status; identifies a transfer.</summary>
    public string? DischargeStatus { get; init; }
}

/// <summary>A priced stay, with the arithmetic that produced it.</summary>
public sealed record MedicaidDrgPrice
{
    /// <summary>Whether the stay could be priced. False means pend, never pay.</summary>
    public required bool Priced { get; init; }

    /// <summary>Why the stay could not be priced.</summary>
    public string? UnpricedReason { get; init; }

    /// <summary>Base rate after any wage adjustment.</summary>
    public decimal AdjustedBaseRate { get; init; }

    /// <summary>Relative weight times any policy adjustors.</summary>
    public decimal PaymentWeight { get; init; }

    /// <summary>The full DRG payment before transfer adjustment.</summary>
    public decimal FullDrgPayment { get; init; }

    /// <summary>The DRG payment after transfer adjustment.</summary>
    public decimal DrgPayment { get; init; }

    /// <summary>Whether the stay was paid as a transfer.</summary>
    public bool TransferAdjusted { get; init; }

    /// <summary>The outlier paid (0 when none). Negative for a Medi-Cal low-side outlier.</summary>
    public decimal OutlierPayment { get; init; }

    /// <summary>Which outlier was paid.</summary>
    public MedicaidOutlierKind OutlierKind { get; init; }

    /// <summary>Whether the payment was reduced to charges.</summary>
    public bool ChargeCapApplied { get; init; }

    /// <summary>The allowed amount, rounded to the cent.</summary>
    public decimal AllowedAmount { get; init; }

    /// <summary>Each step of the calculation, in order, in words.</summary>
    public IReadOnlyList<string> Steps { get; init; } = [];

    internal static MedicaidDrgPrice Unpriced(string reason) => new()
    {
        Priced = false,
        UnpricedReason = reason,
        Steps = [reason],
    };
}

/// <summary>Which outlier, if any, a stay was paid.</summary>
public enum MedicaidOutlierKind
{
    None,
    DayOutlier,
    CostOutlier,
    LowSideCostOutlier,
}

/// <summary>
/// Prices a Medicaid APR-DRG stay under one state's method.
/// </summary>
public interface IMedicaidDrgPricer
{
    /// <summary>
    /// Prices one stay. Never throws for missing data: a stay that cannot be
    /// priced comes back with <see cref="MedicaidDrgPrice.Priced"/> false and
    /// a reason, so adjudication pends it.
    /// </summary>
    MedicaidDrgPrice Price(MedicaidDrgStay stay, MedicaidDrgWeight? weight, MedicaidDrgHospital hospital);
}

internal static class DischargeStatuses
{
    /// <summary>
    /// Medicare IPPS acute-care transfer statuses: to a short-term hospital
    /// (02), to a cancer or children's hospital or other institution (05), to
    /// a critical access hospital (66), and their planned-readmission
    /// equivalents (82, 85, 94).
    /// </summary>
    public static readonly IReadOnlySet<string> MedicareAcuteTransfer =
        new HashSet<string>(StringComparer.Ordinal) { "02", "05", "66", "82", "85", "94" };

    public static string? Normalize(string? status) =>
        string.IsNullOrWhiteSpace(status) ? null : status.Trim().PadLeft(2, '0');
}
