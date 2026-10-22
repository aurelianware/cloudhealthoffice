namespace CloudHealthOffice.GrouperEngine.Ipps;

/// <summary>
/// How a fiscal year's federal rate splits between the portion adjusted by the
/// area wage index and the portion that is not.
///
/// <para>
/// A year has two of these, and which one a hospital gets is a comparison of
/// the wage index against 1.0 — nothing else. 42 CFR 412.64(h)(3) is written
/// differently: it sets the labor-related share at 62 percent "unless employing
/// that percentage would result in lower payments for the hospital" than the
/// share CMS sets in the annual rule, which reads as whichever of the two pays
/// more. Those two readings agree wherever the cost-of-living adjustment is
/// 1.0, and come apart in Alaska and Hawaii, where 412.64(m)(1)(ii) lifts the
/// non-labor share.
/// </para>
///
/// <para>
/// <b>CMS's own pricer implements the threshold, not the comparison</b>, and
/// the paid amount is whatever its program says. An earlier version of this
/// type did the comparison, on the strength of the regulation's wording, and
/// the parity run in <c>tools/cms-ipps-pricer-oracle</c> showed it overpaying:
/// on the corpus's Alaska stay, at a wage index of 1.1438 with a cost-of-living
/// adjustment of 1.25, CMS pays 15,368.43 using the larger labor share while
/// the "whichever pays more" reading would have paid 15,424 using the smaller
/// one. Reading the regulation correctly and paying the claim correctly turned
/// out to be different things, which is the entire argument for the oracle.
/// </para>
/// </summary>
/// <param name="LaborRelated">
/// The share adjusted by the wage index, as a fraction (0.66, not 66).
/// </param>
/// <param name="NonLaborRelated">
/// The share that is not. Held separately rather than derived as
/// <c>1 - LaborRelated</c> because CMS publishes both as dollar amounts and
/// they are rounded independently; deriving one from the other moves the
/// answer by cents on every claim.
/// </param>
public sealed record IppsLaborShare(decimal LaborRelated, decimal NonLaborRelated)
{
    /// <summary>
    /// The multiplier applied to the standardized amount for this share:
    /// the labor portion scaled by the wage index, plus the non-labor portion
    /// scaled by any cost-of-living adjustment.
    /// </summary>
    /// <param name="wageIndex">The hospital's area wage index.</param>
    /// <param name="costOfLiving">
    /// The non-labor cost-of-living adjustment, 1.0 outside Alaska and Hawaii
    /// (42 CFR 412.64(m)(1)(ii)).
    /// </param>
    public decimal Adjust(decimal wageIndex, decimal costOfLiving) =>
        (LaborRelated * wageIndex) + (NonLaborRelated * costOfLiving);
}

/// <summary>
/// The cost outlier policy for a fiscal year (42 CFR 412.84).
/// </summary>
/// <param name="FixedLossAmount">
/// The fixed loss amount added to the case's payment to give the threshold
/// above which an outlier is paid. CMS republishes it every year and it moves
/// by thousands of dollars, which is most of the reason this type exists.
/// </param>
/// <param name="MarginalCostFactor">
/// The share of the cost above the threshold that is paid: 80 percent under
/// 412.84(k).
/// </param>
/// <param name="BurnMarginalCostFactor">
/// 90 percent, for the burn DRGs named in the annual rate notice
/// (412.84(l)).
/// </param>
/// <param name="BurnDrgs">
/// Those DRGs. A list rather than a constant because the annual notice names
/// them and the MS-DRG numbers themselves have been renumbered before.
/// </param>
public sealed record IppsOutlierPolicy(
    decimal FixedLossAmount,
    decimal MarginalCostFactor,
    decimal BurnMarginalCostFactor,
    IReadOnlySet<int> BurnDrgs)
{
    /// <summary>The share of the excess cost paid for this DRG.</summary>
    public decimal FactorFor(int drg) =>
        BurnDrgs.Contains(drg) ? BurnMarginalCostFactor : MarginalCostFactor;
}

/// <summary>
/// The indirect medical education formula for a fiscal year
/// (42 CFR 412.105(c) and (d)).
///
/// <para>
/// The exponent and the multiplier are both in the regulation rather than in
/// the annual rule, and neither has moved since FY2008. They are data anyway:
/// a payment engine that cannot be corrected without a release is a payment
/// engine that pays last year's rates in October.
/// </para>
/// </summary>
/// <param name="Exponent">0.405 (412.105(c)).</param>
/// <param name="Multiplier">1.35 for FY2008 and after (412.105(d)(3)(xii)).</param>
public sealed record IppsTeachingPolicy(double Exponent, decimal Multiplier)
{
    /// <summary>
    /// The education adjustment factor for a resident-to-bed ratio: the three
    /// steps of 412.105(d), which are
    /// <c>((1 + ratio) ^ exponent - 1) × multiplier</c>.
    /// </summary>
    public decimal FactorFor(decimal residentToBedRatio)
    {
        if (residentToBedRatio <= 0)
            return 0m;

        var raised = Math.Pow(1d + (double)residentToBedRatio, Exponent);
        return (decimal)(raised - 1d) * Multiplier;
    }
}

/// <summary>
/// One fiscal year's IPPS rates and policies — everything in the payment that
/// is the same for every hospital.
///
/// <para>
/// None of these numbers are in this repository's code, and that is the point.
/// CMS republishes all of them every August for the October discharges, and an
/// engine with a rate compiled into it is wrong from 1 October until someone
/// ships a release. So a fiscal year is a configuration load, exactly as a
/// grouper version is a table load.
/// </para>
/// </summary>
/// <param name="FiscalYear">The federal fiscal year, e.g. 2026.</param>
/// <param name="FirstDischargeDate">
/// Normally 1 October. A stay is paid by the rates in force on the date of
/// discharge, as it is grouped by the tables in force then.
/// </param>
/// <param name="LastDischargeDate">
/// The last discharge date these rates pay, or null while they are current.
/// </param>
/// <param name="OperatingStandardizedAmount">
/// The federal operating rate, before the wage adjustment and the DRG weight.
/// </param>
/// <param name="HigherLaborShare">
/// The share CMS sets in the annual rule, which pays more where the wage index
/// is above 1.0.
/// </param>
/// <param name="StatutoryLaborShare">
/// The 62 percent share of 412.64(h)(3), which pays more where the wage index
/// is below 1.0.
/// </param>
/// <param name="CapitalFederalRate">The federal capital rate.</param>
/// <param name="Outlier">The cost outlier policy.</param>
/// <param name="Teaching">The indirect medical education formula.</param>
/// <param name="EmpiricallyJustifiedDshShare">
/// The share of the computed DSH adjustment still paid through the claim:
/// 25 percent, because 412.106(f) reduces the 412.106(d) amount by 75 percent
/// for discharges on or after 1 October 2013. The withheld 75 percent funds
/// the uncompensated care pool, which reaches the hospital as a per-discharge
/// amount on its own and not as a percentage of anything
/// (<see cref="IppsProvider.UncompensatedCarePerDischarge"/>).
/// </param>
public sealed record IppsRateSet(
    int FiscalYear,
    DateOnly FirstDischargeDate,
    DateOnly? LastDischargeDate,
    decimal OperatingStandardizedAmount,
    IppsLaborShare HigherLaborShare,
    IppsLaborShare StatutoryLaborShare,
    decimal CapitalFederalRate,
    IppsOutlierPolicy Outlier,
    IppsTeachingPolicy Teaching,
    decimal EmpiricallyJustifiedDshShare)
{
    /// <summary>Whether these rates pay a stay discharged on the date.</summary>
    public bool Covers(DateOnly dischargeDate) =>
        dischargeDate >= FirstDischargeDate
        && (LastDischargeDate is null || dischargeDate <= LastDischargeDate);

    /// <summary>
    /// The labor share for a wage index: the annual rule's larger share above
    /// 1.0, and the statutory 62 percent at or below it.
    ///
    /// <para>
    /// The cost-of-living adjustment is deliberately not a parameter. It scales
    /// the non-labor side of the payment, so it does affect which share pays
    /// more — but it does not affect which share CMS chooses, and choosing the
    /// other one overpays. See the note on <see cref="IppsLaborShare"/>.
    /// </para>
    /// </summary>
    public IppsLaborShare ShareFor(decimal wageIndex) =>
        wageIndex > 1m ? HigherLaborShare : StatutoryLaborShare;
}

/// <summary>
/// Supplies the rates in force for a discharge date.
/// </summary>
public interface IIppsRateProvider
{
    /// <summary>
    /// The rates paying a stay discharged on the date, or null when the
    /// deployment has loaded none that cover it.
    /// </summary>
    Task<IppsRateSet?> GetForDischargeDateAsync(
        DateOnly dischargeDate, CancellationToken cancellationToken = default);
}

/// <summary>Chooses between the fiscal years a deployment has loaded.</summary>
public sealed class IppsRateYears : IIppsRateProvider
{
    private readonly IReadOnlyList<IppsRateSet> years;

    /// <summary>Creates a provider over the loaded fiscal years, in any order.</summary>
    public IppsRateYears(IEnumerable<IppsRateSet> years)
    {
        ArgumentNullException.ThrowIfNull(years);
        this.years = [.. years];
    }

    /// <inheritdoc />
    public Task<IppsRateSet?> GetForDischargeDateAsync(
        DateOnly dischargeDate, CancellationToken cancellationToken = default) =>
        Task.FromResult(years.FirstOrDefault(year => year.Covers(dischargeDate)));
}
