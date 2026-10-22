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
/// <param name="QualifiesForHalfAndHalfTransfer">
/// Whether this DRG is one the post-acute transfer rules pay half the full
/// amount plus half the per diem for (412.4(f)(5), (f)(6)). A property of the
/// DRG in the annual rule — a geometric mean above 4 days and one-day
/// discharge charges at least half the DRG's average — so it arrives as an
/// answer rather than being worked out here.
/// </param>
public sealed record IppsStay(
    int Drg,
    decimal RelativeWeight,
    decimal GeometricMeanLengthOfStay,
    int LengthOfStayDays,
    decimal CoveredCharges,
    IppsDischargeKind Kind,
    bool QualifiesForHalfAndHalfTransfer = false);

/// <summary>
/// The operating side of the payment, itemised.
/// </summary>
/// <param name="WageAdjustedStandardizedAmount">
/// The federal operating rate after the labor split and the wage index.
/// </param>
/// <param name="LaborRelatedShare">Which of the year's two shares was used.</param>
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
    decimal WageAdjustedStandardizedAmount,
    decimal LaborRelatedShare,
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
/// <param name="PerDiem">The full payment divided by the DRG's geometric mean stay.</param>
/// <param name="DaysPaid">
/// The per diems paid. The first day is paid twice under 412.4(f)(1), so this
/// is the length of stay plus one.
/// </param>
/// <param name="CappedAtFullPayment">
/// Whether the per diem arithmetic reached the full DRG payment and was capped
/// there, which 412.4(f)(1) requires. True on any transfer that stayed at
/// least as long as the DRG's geometric mean, so not an edge case.
/// </param>
/// <param name="HalfAndHalf">
/// Whether the half-the-full-amount-plus-half-the-per-diem rule applied
/// (412.4(f)(2), (f)(5), (f)(6)).
/// </param>
public sealed record IppsTransferReduction(
    decimal PerDiem,
    int DaysPaid,
    bool CappedAtFullPayment,
    bool HalfAndHalf);

/// <summary>
/// A cost outlier, and the arithmetic that found one.
/// </summary>
/// <param name="EstimatedCost">
/// Charges times the cost-to-charge ratio, operating and capital together.
/// </param>
/// <param name="Threshold">
/// The case's payment plus the year's fixed loss amount, the fixed loss amount
/// wage-adjusted by the same labor split as the standardized amount.
/// </param>
/// <param name="MarginalCostFactor">80 percent, or 90 for a burn DRG.</param>
/// <param name="Payment">The outlier payment, zero when the cost did not reach the threshold.</param>
public sealed record IppsOutlier(
    decimal EstimatedCost,
    decimal Threshold,
    decimal MarginalCostFactor,
    decimal Payment);

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
/// <b>Not yet validated against CMS's PRICER.</b> Every formula here is taken
/// from the regulation rather than from memory, and the paragraph is cited at
/// each one — but reading a formula correctly and implementing it the way
/// CMS's own program does are different claims, and only the second one gets a
/// payment right. The grouper next door is trustworthy because it was run
/// against CMS's grouper and agreed on every case, not because it was written
/// carefully. This needs the same treatment before a plan pays on it, and
/// <see cref="IppsPayment"/> keeps every intermediate so that run can compare
/// step by step rather than just comparing totals.
/// </para>
///
/// <para>
/// Two things are deliberately absent and would be wrong to approximate: new
/// technology add-on payments, which are a published list of devices and
/// amounts, and the low-volume and Medicare-dependent hospital adjustments,
/// which turn on a hospital's status rather than on the case.
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

        var share = rates.ShareFor(provider.WageIndex, provider.CostOfLivingAdjustment);
        var wageAdjusted = rates.OperatingStandardizedAmount
            * share.Adjust(provider.WageIndex, provider.CostOfLivingAdjustment);

        var operatingBase = wageAdjusted * stay.RelativeWeight;
        var teachingFactor = rates.Teaching.FactorFor(provider.ResidentToBedRatio);

        // 412.105(e)(1) applies the education factor to DRG revenue for
        // operating costs, and 412.105(a)(2) excludes outlier and 412.106
        // payments from that revenue. So the add-ons are both taken on the
        // base payment and neither compounds the other.
        var dshFactor = provider.OperatingDshAdjustment * rates.EmpiricallyJustifiedDshShare;

        var operating = new IppsOperatingPayment(
            wageAdjusted,
            share.LaborRelated,
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

        var fullPayment = operating.Total + capital.Total;
        var (paid, transfer) = ApplyTransfer(stay, fullPayment, notes);

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

        var outlier = Outlier(rates, provider, stay, paid, share);
        if (outlier.Payment > 0)
        {
            notes.Add(
                $"Cost outlier: an estimated cost of {outlier.EstimatedCost:C} against a threshold "
                + $"of {outlier.Threshold:C}, paid at {outlier.MarginalCostFactor:P0} of the excess.");
        }

        return new IppsPayment(rates.FiscalYear, operating, capital, transfer, outlier, notes)
        {
            PaymentBeforeOutliers = paid
        };
    }

    private static (decimal Paid, IppsTransferReduction? Transfer) ApplyTransfer(
        IppsStay stay, decimal fullPayment, List<string> notes)
    {
        if (stay.Kind == IppsDischargeKind.Discharged)
            return (fullPayment, null);

        if (stay.GeometricMeanLengthOfStay <= 0)
        {
            throw new IppsPaymentFailedException(
                $"DRG {stay.Drg:000} has no geometric mean length of stay, so the per diem a "
                + "transfer is paid by cannot be computed.");
        }

        var perDiem = fullPayment / stay.GeometricMeanLengthOfStay;

        // 412.4(f)(1): twice the per diem for the first day, then one per diem
        // for each later day, up to the full DRG payment. Which is the length
        // of stay plus one, and the cap is reached by any transfer that stayed
        // as long as the DRG's geometric mean.
        var graduated = perDiem * (stay.LengthOfStayDays + 1);
        var daysPaid = stay.LengthOfStayDays + 1;

        var halfAndHalf = stay.Kind == IppsDischargeKind.TransferredToPostAcuteCare
            && stay.QualifiesForHalfAndHalfTransfer;

        // 412.4(f)(2): 50 percent of the full rate for the first day, plus 50
        // percent of the (f)(1) amount, capped at the full payment. The
        // regulation's "50 percent of the amount calculated under (f)(1) for
        // each day" is read here as half of the whole (f)(1) amount, which is
        // how the industry implements it -- but the wording admits a reading
        // where the halving is per day, and the two differ. This is one of the
        // lines a PRICER parity run has to settle.
        var amount = halfAndHalf
            ? (0.5m * fullPayment) + (0.5m * Math.Min(graduated, fullPayment))
            : graduated;

        var capped = amount >= fullPayment;
        var paid = Math.Min(amount, fullPayment);

        notes.Add(halfAndHalf
            ? $"Post-acute transfer on a qualifying DRG: half the full payment plus half the "
              + $"per diem amount, {(capped ? "capped at the full payment" : "under the cap")}."
            : $"Transfer paid {daysPaid} per diems of {perDiem:C}"
              + $"{(capped ? ", capped at the full payment" : "")}.");

        return (paid, new IppsTransferReduction(perDiem, daysPaid, capped, halfAndHalf));
    }

    /// <summary>
    /// The case's cost outlier (42 CFR 412.84(k), (l)).
    ///
    /// <para>
    /// One outlier, not two. 412.84 reads as though operating and capital were
    /// tested separately against their own thresholds, and the older form of
    /// the policy did exactly that — but the fixed loss amount CMS publishes is
    /// a single annual figure for the case, so testing each side against the
    /// payment-plus-that-amount counts the threshold twice and pays an outlier
    /// on cases that are nowhere near one. The two cost-to-charge ratios are
    /// still separate, because 412.84(g) keeps them separate and a hospital's
    /// two ratios differ; they are applied to the charges and added.
    /// </para>
    ///
    /// <para>
    /// The threshold is measured from what the case is actually paid, so a
    /// transfer's reduced payment lowers it — which is the behaviour 412.4(f)(4)
    /// implies by allowing an outlier on a transfer at all.
    /// </para>
    /// </summary>
    private static IppsOutlier Outlier(
        IppsRateSet rates,
        IppsProvider provider,
        IppsStay stay,
        decimal payment,
        IppsLaborShare share)
    {
        var ratio = provider.OperatingCostToChargeRatio + provider.CapitalCostToChargeRatio;

        // A hospital with no ratio on file gets no outlier, rather than one
        // computed from a cost of zero. The same answer today, and the right
        // one if the arithmetic below ever changes.
        if (ratio <= 0)
            return new IppsOutlier(0m, 0m, 0m, 0m);

        var cost = stay.CoveredCharges * ratio;

        // The fixed loss amount is wage-adjusted on the same split as the
        // standardized amount, so that the threshold sits at the same distance
        // above the payment in a high-wage area as in a low-wage one.
        var fixedLoss = rates.Outlier.FixedLossAmount
            * share.Adjust(provider.WageIndex, provider.CostOfLivingAdjustment);

        var threshold = payment + fixedLoss;
        var factor = rates.Outlier.FactorFor(stay.Drg);

        return new IppsOutlier(
            cost,
            threshold,
            factor,
            Payment: cost > threshold ? factor * (cost - threshold) : 0m);
    }
}
