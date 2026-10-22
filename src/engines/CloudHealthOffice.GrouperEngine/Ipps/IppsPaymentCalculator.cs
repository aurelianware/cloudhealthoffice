namespace CloudHealthOffice.GrouperEngine.Ipps;

/// <summary>
/// How a stay ended, so far as the payment is concerned.
/// </summary>
public enum IppsDischargeKind
{
    /// <summary>
    /// Discharged. The hospital is paid the full DRG amount, which is what
    /// happens on the great majority of stays.
    /// </summary>
    Discharged,

    /// <summary>
    /// Transferred to another acute care hospital paid under IPPS
    /// (42 CFR 412.4(b)). Paid a graduated per diem.
    /// </summary>
    TransferredToAcuteCare,

    /// <summary>
    /// Transferred to post-acute care — a skilled nursing facility, home
    /// health, a rehabilitation or psychiatric unit (412.4(c)). Paid a
    /// graduated per diem, or the half-and-half amount where the DRG
    /// qualifies.
    /// </summary>
    TransferredToPostAcuteCare
}

/// <summary>
/// What a stay has to say about itself before it can be paid.
/// </summary>
/// <param name="Drg">The MS-DRG, which the grouper assigned.</param>
/// <param name="RelativeWeight">The DRG's relative weight.</param>
/// <param name="GeometricMeanLengthOfStay">
/// The DRG's geometric mean length of stay, which is what a transfer per diem
/// divides by (412.4(f)(1)). Must be above zero for a transfer.
/// </param>
/// <param name="LengthOfStayDays">The days the patient spent in this hospital.</param>
/// <param name="CoveredCharges">
/// The hospital's billed charges for covered inpatient services, which the
/// cost-to-charge ratios turn into an estimated cost for the outlier test
/// (412.84(g)).
/// </param>
/// <param name="Kind">How the stay ended.</param>
/// <param name="PostAcuteTransferRule">
/// Which post-acute transfer rule this DRG carries. Read from the DRG table
/// beside the weight and the geometric mean, not decided here and not asserted
/// by the caller: CMS's own pricer takes it from two columns of its DRG table
/// and ignores what the claim says about it.
/// </param>
public sealed record IppsStay(
    int Drg,
    decimal RelativeWeight,
    decimal GeometricMeanLengthOfStay,
    int LengthOfStayDays,
    decimal CoveredCharges,
    IppsDischargeKind Kind,
    IppsPostAcuteTransferRule PostAcuteTransferRule = IppsPostAcuteTransferRule.None);

/// <summary>
/// Which post-acute transfer rule a DRG carries (42 CFR 412.4(f)).
///
/// <para>
/// A property of the DRG, published in the annual rule and held in the DRG
/// table as two columns. An earlier version of <see cref="IppsStay"/> had the
/// caller assert the half-and-half case as a boolean, which is the same
/// misplacement as deriving a hospital's disproportionate patient percentage
/// from a claim: the answer exists, it is published, and working it out at
/// payment time can only get it wrong.
/// </para>
/// </summary>
public enum IppsPostAcuteTransferRule
{
    /// <summary>Not a qualifying DRG: a post-acute transfer is paid in full.</summary>
    None = 0,

    /// <summary>The graduated per diem of 412.4(f)(1).</summary>
    PerDiem,

    /// <summary>Half the full payment plus half the per diem, 412.4(f)(2).</summary>
    HalfAndHalf
}

/// <summary>
/// The operating side of the payment, itemised.
/// </summary>
/// <param name="StandardizedAmount">
/// The row of the year's operating rate table this hospital was paid from,
/// before the wage index. Kept rather than reduced to the one adjusted figure
/// because which row was chosen is three facts about the hospital, and a
/// payment that disagrees with CMS by a few percent usually disagrees about
/// one of them.
/// </param>
/// <param name="WageAdjustedStandardizedAmount">
/// That row after the wage index and any cost-of-living adjustment.
/// </param>
/// <param name="BasePayment">
/// The wage-adjusted amount times the DRG weight: the DRG payment before any
/// add-on.
/// </param>
/// <param name="TeachingAddOn">The indirect medical education payment.</param>
/// <param name="DisproportionateShareAddOn">
/// The empirically justified disproportionate share payment — a quarter of the
/// 412.106(d) amount, the rest having moved to uncompensated care.
/// </param>
/// <param name="UncompensatedCarePayment">
/// The per-discharge uncompensated care payment (412.106(g)).
/// </param>
public sealed record IppsOperatingPayment(
    IppsStandardizedAmount StandardizedAmount,
    decimal WageAdjustedStandardizedAmount,
    decimal BasePayment,
    decimal TeachingAddOn,
    decimal DisproportionateShareAddOn,
    decimal UncompensatedCarePayment)
{
    /// <summary>The operating payment before any transfer reduction or outlier.</summary>
    public decimal Total =>
        BasePayment + TeachingAddOn + DisproportionateShareAddOn + UncompensatedCarePayment;
}

/// <summary>
/// The capital side of the payment, itemised.
/// </summary>
/// <param name="GeographicallyAdjustedRate">
/// The federal capital rate times the geographic adjustment factor.
/// </param>
/// <param name="BasePayment">That rate times the DRG weight.</param>
/// <param name="TeachingAddOn">The capital indirect medical education payment.</param>
/// <param name="DisproportionateShareAddOn">The capital disproportionate share payment.</param>
public sealed record IppsCapitalPayment(
    decimal GeographicallyAdjustedRate,
    decimal BasePayment,
    decimal TeachingAddOn,
    decimal DisproportionateShareAddOn)
{
    /// <summary>The capital payment before any transfer reduction or outlier.</summary>
    public decimal Total => BasePayment + TeachingAddOn + DisproportionateShareAddOn;
}

/// <summary>
/// How a transfer reduced the payment, or why it did not.
/// </summary>
/// <param name="Fraction">
/// The fraction of the full payment this transfer is paid, after the cap.
/// </param>
/// <param name="DaysPaid">
/// The per diems paid. The first day is paid twice under 412.4(f)(1), and CMS
/// floors the length of stay at one day before adding it, so this is
/// <c>max(length of stay, 1) + 1</c>.
/// </param>
/// <param name="CappedAtFullPayment">
/// Whether the per diem arithmetic reached the full DRG payment and was capped
/// there, which 412.4(f)(1) requires. True on any transfer that stayed at
/// least as long as the DRG's geometric mean -- and, because of that extra
/// first day, on every stay in a DRG whose geometric mean is under two days,
/// where no transfer reduction is possible at all.
/// </param>
/// <param name="HalfAndHalf">
/// Whether the half-the-full-amount-plus-half-the-per-diem rule applied
/// (412.4(f)(2), (f)(5), (f)(6)).
/// </param>
public sealed record IppsTransferReduction(
    decimal Fraction,
    int DaysPaid,
    bool CappedAtFullPayment,
    bool HalfAndHalf);

/// <summary>
/// One side's cost outlier, and the arithmetic that found it.
/// </summary>
/// <param name="EstimatedCost">
/// This side's charges times this side's cost-to-charge ratio.
/// </param>
/// <param name="FixedLossShare">
/// This side's portion of the year's single fixed loss amount, apportioned by
/// cost-to-charge ratio and then adjusted by this side's own geographic factor.
/// </param>
/// <param name="Threshold">This side's payment plus that portion.</param>
/// <param name="Payment">
/// The outlier payment, zero when the cost did not reach the threshold.
/// </param>
public sealed record IppsOutlierSide(
    decimal EstimatedCost,
    decimal FixedLossShare,
    decimal Threshold,
    decimal Payment);

/// <summary>
/// A cost outlier (42 CFR 412.84).
///
/// <para>
/// Two thresholds from one fixed loss amount, which is the shape that took
/// three attempts to get right. The first draft here tested operating and
/// capital each against the <i>whole</i> fixed loss amount, which counts it
/// twice and pays outliers on ordinary cases. The correction went too far the
/// other way, adding the two costs and testing them against one threshold. CMS
/// does neither: it apportions the single fixed loss amount between the two
/// sides <b>in proportion to the hospital's two cost-to-charge ratios</b>, so
/// the shares sum to one and the amount is counted exactly once, and then
/// adjusts each side by its own geographic factor — the wage index and
/// cost-of-living split for operating, the capital geographic adjustment factor
/// for capital. Those factors differ, so the apportionment is not cosmetic.
/// </para>
///
/// <para>
/// This is the structure the parity run in <c>tools/cms-ipps-pricer-oracle</c>
/// established, and it reproduces CMS's outlier on the corpus's cost-outlier
/// stay to within rounding on the operating side and exactly on the capital
/// side.
/// </para>
/// </summary>
/// <param name="Operating">The operating side.</param>
/// <param name="Capital">The capital side.</param>
/// <param name="MarginalCostFactor">80 percent, or 90 for a burn DRG.</param>
public sealed record IppsOutlier(
    IppsOutlierSide Operating,
    IppsOutlierSide Capital,
    decimal MarginalCostFactor)
{
    /// <summary>No outlier: the hospital has no cost-to-charge ratio on file.</summary>
    public static readonly IppsOutlier None = new(
        new IppsOutlierSide(0m, 0m, 0m, 0m), new IppsOutlierSide(0m, 0m, 0m, 0m), 0m);

    /// <summary>Both sides' estimated cost.</summary>
    public decimal EstimatedCost => Operating.EstimatedCost + Capital.EstimatedCost;

    /// <summary>The outlier paid, both sides together.</summary>
    public decimal Payment => Operating.Payment + Capital.Payment;
}

/// <summary>
/// What a stay is paid under IPPS, with every intermediate kept.
///
/// <para>
/// The itemisation is the point. A hospital that disputes a payment disputes
/// one of these lines, and an engine that returned only a total could not
/// answer which. It is also what a parity run against CMS's own PRICER needs:
/// a single total that agrees by luck is indistinguishable from one that
/// agrees because every step did.
/// </para>
/// </summary>
/// <param name="FiscalYear">The rates used.</param>
/// <param name="Operating">The operating side.</param>
/// <param name="Capital">The capital side.</param>
/// <param name="Transfer">The transfer reduction, or null on a discharge.</param>
/// <param name="Outlier">The cost outlier.</param>
/// <param name="Notes">What a reader of the payment should know about it.</param>
public sealed record IppsPayment(
    int FiscalYear,
    IppsOperatingPayment Operating,
    IppsCapitalPayment Capital,
    IppsTransferReduction? Transfer,
    IppsOutlier Outlier,
    IReadOnlyList<string> Notes)
{
    /// <summary>The full DRG payment, before any transfer reduction or outlier.</summary>
    public decimal FullPayment => Operating.Total + Capital.Total;

    /// <summary>
    /// What the case is paid after the transfer reduction, and so the amount
    /// the outlier threshold is measured from.
    /// </summary>
    public decimal PaymentBeforeOutliers { get; init; }

    /// <summary>The total paid.</summary>
    public decimal Total => PaymentBeforeOutliers + Outlier.Payment;
}

/// <summary>Raised when a stay cannot be paid from the data given.</summary>
public sealed class IppsPaymentFailedException(string message) : Exception(message);

/// <summary>
/// Pays an inpatient stay under the Medicare Inpatient Prospective Payment
/// System: the wage adjustment, the teaching and disproportionate share
/// add-ons, the capital payment, the transfer per diems and the cost outliers.
///
/// <para>
/// <b>Validated against CMS's own pricer</b> on the base DRG payment, the
/// standardized amount and its labor split, every transfer fraction and the
/// capital outlier: <c>tools/cms-ipps-pricer-oracle</c> prices a reference
/// corpus through the CMS program and
/// <c>IppsPricerParityTests</c> holds this code to those amounts on every
/// build. That matters more than the care taken writing it — every formula
/// here cites the paragraph of 42 CFR that it implements, and the parity run
/// still reversed one of them, because reading a regulation correctly and
/// paying a claim the way CMS's program pays it are different claims and only
/// the second one is the amount.
/// </para>
///
/// <para>
/// <b>This is still not a Medicare payment.</b> Components CMS pays and this
/// does not: the hospital readmissions reduction, the value-based purchasing
/// adjustment, the low-volume adjustment and the hospital-acquired-condition
/// penalty — all four of which are signed amounts in CMS's total and are taken
/// in an order that is not derivable from the regulation — the
/// hospital-specific portion for sole-community and
/// Medicare-dependent hospitals, pass-through amounts for direct medical
/// education and organ acquisition, new-technology add-ons (a published list
/// of devices and amounts, so an importer rather than a formula), and the
/// bundled-payment adjustment. <see cref="IppsPayment"/> keeps every
/// intermediate, which is what lets the parity run find a disagreement in one
/// line rather than in a total.
/// </para>
/// </summary>
public static class IppsPaymentCalculator
{
    /// <summary>
    /// Pays a stay.
    /// </summary>
    /// <exception cref="IppsPaymentFailedException">
    /// When the stay is a transfer and the DRG has no geometric mean length of
    /// stay to divide by, which is the one input whose absence has no sensible
    /// answer: treating it as a discharge overpays, and treating the per diem
    /// as zero pays nothing.
    /// </exception>
    public static IppsPayment Pay(IppsRateSet rates, IppsProvider provider, IppsStay stay)
    {
        ArgumentNullException.ThrowIfNull(rates);
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(stay);

        var notes = new List<string>();

        // The DRG is paid from the dollar amounts in the rate table row this
        // hospital selects; the percentage pair below is for the outlier
        // threshold alone, which is the only place CMS uses it.
        var standardizedAmount = rates.StandardizedAmountFor(provider);
        var percentages = rates.PercentagesFor(provider.WageIndex);
        var wageAdjusted = standardizedAmount.Adjust(
            provider.WageIndex, provider.CostOfLivingAdjustment);

        var operatingBase = wageAdjusted * stay.RelativeWeight;
        var teachingFactor = rates.Teaching.FactorFor(provider.ResidentToBedRatio);

        // 412.105(e)(1) applies the education factor to DRG revenue for
        // operating costs, and 412.105(a)(2) excludes outlier and 412.106
        // payments from that revenue. So the add-ons are both taken on the
        // base payment and neither compounds the other.
        var dshFactor = provider.OperatingDshAdjustment * rates.EmpiricallyJustifiedDshShare;

        var operating = new IppsOperatingPayment(
            standardizedAmount,
            wageAdjusted,
            operatingBase,
            TeachingAddOn: operatingBase * teachingFactor,
            DisproportionateShareAddOn: operatingBase * dshFactor,
            UncompensatedCarePayment: provider.UncompensatedCarePerDischarge);

        var capitalRate = rates.CapitalFederalRate * provider.CapitalGeographicAdjustmentFactor;
        var capitalBase = capitalRate * stay.RelativeWeight;
        var capital = new IppsCapitalPayment(
            capitalRate,
            capitalBase,
            TeachingAddOn: capitalBase * provider.CapitalIndirectMedicalEducationFactor,
            DisproportionateShareAddOn: capitalBase * provider.CapitalDisproportionateShareFactor);

        // A transfer reduces the payment by a fraction, and each side is
        // reduced by it separately, because each side's outlier threshold is
        // then measured from its own reduced payment. The uncompensated care
        // payment is left out of the reduction: 412.106(g) pays it per
        // discharge, so nothing about it scales with the length of the stay.
        var (fraction, transfer) = TransferFraction(stay, notes);
        var operatingPaid =
            (operating.BasePayment + operating.TeachingAddOn + operating.DisproportionateShareAddOn)
                * fraction
            + operating.UncompensatedCarePayment;
        var capitalPaid = capital.Total * fraction;
        var paid = operatingPaid + capitalPaid;

        if (provider.ResidentToBedRatio > 0)
        {
            notes.Add(
                $"Indirect medical education paid at {teachingFactor:P2} of the base operating "
                + $"payment, on a resident-to-bed ratio of {provider.ResidentToBedRatio:0.####}.");
        }

        if (provider.OperatingDshAdjustment > 0)
        {
            notes.Add(
                $"Disproportionate share paid at {dshFactor:P2}: the hospital's "
                + $"{provider.OperatingDshAdjustment:P2} adjustment reduced to the "
                + $"{rates.EmpiricallyJustifiedDshShare:P0} still paid through the claim.");
        }

        var outlier = Outlier(rates, provider, stay, operatingPaid, capitalPaid, percentages);
        if (outlier.Payment > 0)
        {
            notes.Add(
                $"Cost outlier of {outlier.Payment:C} at {outlier.MarginalCostFactor:P0} of the "
                + $"excess: an operating cost of {outlier.Operating.EstimatedCost:C} against a "
                + $"threshold of {outlier.Operating.Threshold:C}, and a capital cost of "
                + $"{outlier.Capital.EstimatedCost:C} against {outlier.Capital.Threshold:C}.");
        }

        return new IppsPayment(rates.FiscalYear, operating, capital, transfer, outlier, notes)
        {
            PaymentBeforeOutliers = paid
        };
    }

    /// <summary>
    /// The fraction of the full payment a transfer is paid, and how it was
    /// arrived at. One on a discharge, and on a transfer that reached the cap.
    /// </summary>
    private static (decimal Fraction, IppsTransferReduction? Transfer) TransferFraction(
        IppsStay stay, List<string> notes)
    {
        if (stay.Kind == IppsDischargeKind.Discharged)
            return (1m, null);

        if (stay.GeometricMeanLengthOfStay <= 0)
        {
            // Stricter than CMS, which leaves the payment unreduced in this
            // case. Deliberately so: a DRG with no geometric mean reaching a
            // transfer means the DRG table is wrong, and paying the stay in
            // full hides that behind a plausible amount.
            throw new IppsPaymentFailedException(
                $"DRG {stay.Drg:000} has no geometric mean length of stay, so the per diem a "
                + "transfer is paid by cannot be computed.");
        }

        // 412.4(f)(1): twice the per diem for the first day, then one per diem
        // for each later day. That is the length of stay plus one — except that
        // CMS floors the length of stay at one day first, so a same-day
        // transfer is paid two per diems and not one. The corpus's
        // transfer-zero-length-of-stay row is there because this code used to
        // pay that stay half what CMS pays it.
        var daysPaid = Math.Max(stay.LengthOfStayDays, 1) + 1;
        var graduated = daysPaid / stay.GeometricMeanLengthOfStay;

        var halfAndHalf = stay.PostAcuteTransferRule == IppsPostAcuteTransferRule.HalfAndHalf
            && stay.Kind == IppsDischargeKind.TransferredToPostAcuteCare;

        // 412.4(f)(2): half the full rate plus half the (f)(1) amount. The
        // regulation's "50 percent of the amount calculated under (f)(1) for
        // each day" admits a second reading, where the halving is per day, and
        // the two differ. CMS computes `0.5 + ((0.5 x per diem days) / mean)`,
        // which is the first reading, so the ambiguity is settled and this is
        // no longer a guess.
        var fraction = halfAndHalf ? 0.5m + (0.5m * graduated) : graduated;

        var capped = fraction >= 1m;
        fraction = Math.Min(fraction, 1m);

        notes.Add(halfAndHalf
            ? $"Post-acute transfer on a qualifying DRG: half the full payment plus half of "
              + $"{daysPaid} per diems out of {stay.GeometricMeanLengthOfStay:0.#}"
              + $"{(capped ? ", capped at the full payment" : "")}."
            : $"Transfer paid {daysPaid} per diems out of "
              + $"{stay.GeometricMeanLengthOfStay:0.#}"
              + $"{(capped ? ", capped at the full payment" : "")}.");

        return (fraction, new IppsTransferReduction(fraction, daysPaid, capped, halfAndHalf));
    }

    /// <summary>
    /// The case's cost outlier (42 CFR 412.84(k), (l)), tested on each side
    /// against its own share of the year's fixed loss amount.
    ///
    /// <para>
    /// Each side's threshold is measured from what that side is actually paid,
    /// so a transfer's reduced payment lowers it — which is the behaviour
    /// 412.4(f)(4) implies by allowing an outlier on a transfer at all. See
    /// <see cref="IppsOutlier"/> for why the fixed loss amount is apportioned
    /// rather than applied whole or combined.
    /// </para>
    /// </summary>
    private static IppsOutlier Outlier(
        IppsRateSet rates,
        IppsProvider provider,
        IppsStay stay,
        decimal operatingPayment,
        decimal capitalPayment,
        IppsLaborShare percentages)
    {
        var operatingRatio = provider.OperatingCostToChargeRatio;
        var capitalRatio = provider.CapitalCostToChargeRatio;
        var ratio = operatingRatio + capitalRatio;

        // A hospital with no ratio on file gets no outlier, rather than one
        // computed from a cost of zero. It also keeps the apportionment below
        // from dividing by zero, which is the other reason to test it here.
        if (ratio <= 0)
            return IppsOutlier.None;

        var factor = rates.Outlier.FactorFor(stay.Drg);
        var fixedLoss = rates.Outlier.FixedLossAmount;

        // The operating side's share of the fixed loss amount, wage-adjusted
        // by the year's labor percentages. This is the one place the
        // percentages are used, and CMS uses them here and nowhere else: its
        // threshold is the cost threshold times the percentages, with the
        // standardized amount playing no part.
        var operatingFixedLoss = fixedLoss
            * (operatingRatio / ratio)
            * percentages.Adjust(provider.WageIndex, provider.CostOfLivingAdjustment);

        // The capital side's share, adjusted by the capital geographic
        // adjustment factor instead. Capital has no labor split: the whole
        // capital rate is geographically adjusted as one.
        var capitalFixedLoss = fixedLoss
            * (capitalRatio / ratio)
            * provider.CapitalGeographicAdjustmentFactor
            * provider.CostOfLivingAdjustment;

        return new IppsOutlier(
            Side(stay.CoveredCharges * operatingRatio, operatingFixedLoss, operatingPayment),
            Side(stay.CoveredCharges * capitalRatio, capitalFixedLoss, capitalPayment),
            factor);

        IppsOutlierSide Side(decimal cost, decimal fixedLossShare, decimal payment)
        {
            var threshold = payment + fixedLossShare;
            return new IppsOutlierSide(
                cost,
                fixedLossShare,
                threshold,
                Payment: cost > threshold ? factor * (cost - threshold) : 0m);
        }
    }
}
