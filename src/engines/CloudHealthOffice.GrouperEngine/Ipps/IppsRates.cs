namespace CloudHealthOffice.GrouperEngine.Ipps;

/// <summary>
/// How a fiscal year's federal rate splits between the portion adjusted by the
/// area wage index and the portion that is not.
///
/// <para>
/// A year has two of these, and which one a hospital gets is not a lookup.
/// 42 CFR 412.64(h)(3) sets the labor-related share at 62 percent "unless
/// employing that percentage would result in lower payments for the hospital"
/// than the share CMS sets in the annual rule. So the rule is written as
/// whichever of the two pays more, and it happens to reduce to "the higher
/// share when the wage index is above 1.0" — which is how the trade press
/// states it, and why a calculator that hard-codes the comparison against 1.0
/// is right by accident rather than by the regulation.
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
    /// The labor share that pays this hospital more, which is what
    /// 412.64(h)(3) entitles it to.
    /// </summary>
    public IppsLaborShare ShareFor(decimal wageIndex, decimal costOfLiving) =>
        HigherLaborShare.Adjust(wageIndex, costOfLiving)
            >= StatutoryLaborShare.Adjust(wageIndex, costOfLiving)
            ? HigherLaborShare
            : StatutoryLaborShare;
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
