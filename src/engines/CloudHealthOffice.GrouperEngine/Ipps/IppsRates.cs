namespace CloudHealthOffice.GrouperEngine.Ipps;

/// <summary>
/// One row of the federal operating rate table: the standardized amount split
/// into the portion adjusted by the area wage index and the portion that is
/// not, <b>as dollars</b>.
///
/// <para>
/// Two amounts rather than one amount and a percentage, because that is how
/// CMS publishes them and the two are rounded independently. For FY2026 the
/// electronic-health-record-reduced row at a wage index above 1.0 is
/// 4,349.21 labor and 2,240.51 non-labor. They sum to 6,589.72 exactly, but
/// neither is that sum times a percentage: 6,589.72 × 0.66 is 4,349.2152 and
/// 6,589.72 × 0.34 is 2,240.5048, so deriving either one from a standardized
/// amount and a share moves the answer by a cent on every claim in that row.
/// An earlier version of this file held one standardized amount and a pair of
/// fractions and did exactly that.
/// </para>
/// </summary>
/// <param name="LaborRelated">The labor-related amount, in dollars.</param>
/// <param name="NonLaborRelated">The non-labor-related amount, in dollars.</param>
public sealed record IppsStandardizedAmount(decimal LaborRelated, decimal NonLaborRelated)
{
    /// <summary>The two amounts together, which is the year's base rate.</summary>
    public decimal Total => LaborRelated + NonLaborRelated;

    /// <summary>
    /// The wage-adjusted standardized amount: the labor portion scaled by the
    /// wage index, plus the non-labor portion scaled by any cost-of-living
    /// adjustment.
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
/// The labor and non-labor <b>percentages</b> for a wage index — 66 and 34
/// percent above 1.0, and the statutory 62 and 38 at or below it.
///
/// <para>
/// These are not what pays the DRG. The DRG is paid from the dollar amounts in
/// <see cref="IppsOperatingRateTable"/>, which already carry the split. The
/// percentages have one job: CMS's outlier threshold is the year's cost
/// threshold times these percentages, wage- and cost-of-living-adjusted, and
/// nothing in that formula mentions the standardized amount. So they are held
/// here for that, and a reader who finds them being multiplied by a rate
/// should suspect the rate.
/// </para>
///
/// <para>
/// Which pair applies is a comparison of the wage index against 1.0 — nothing
/// else. 42 CFR 412.64(h)(3) is written differently: it sets the labor-related
/// share at 62 percent "unless employing that percentage would result in lower
/// payments for the hospital" than the share CMS sets in the annual rule, which
/// reads as whichever of the two pays more. The two readings agree wherever the
/// cost-of-living adjustment is 1.0 and come apart in Alaska and Hawaii, where
/// 412.64(m)(1)(ii) lifts the non-labor share.
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
/// The labor-related percentage as a fraction (0.66, not 66).
/// </param>
/// <param name="NonLaborRelated">The non-labor percentage, likewise.</param>
public sealed record IppsLaborShare(decimal LaborRelated, decimal NonLaborRelated)
{
    /// <summary>
    /// The multiplier these percentages apply to a dollar amount: the labor
    /// portion scaled by the wage index, plus the non-labor portion scaled by
    /// any cost-of-living adjustment.
    /// </summary>
    /// <param name="wageIndex">The hospital's area wage index.</param>
    /// <param name="costOfLiving">The non-labor cost-of-living adjustment.</param>
    public decimal Adjust(decimal wageIndex, decimal costOfLiving) =>
        (LaborRelated * wageIndex) + (NonLaborRelated * costOfLiving);
}

/// <summary>
/// Which row of the federal operating rate table pays a stay.
///
/// <para>
/// Three independent facts, so eight rows. CMS's pricer is a straight eight-way
/// branch on exactly these — <c>2050-RATES-TB</c> in the COBOL, split across
/// <c>DetermineRateTableWithoutReduction</c> and
/// <c>DetermineRateTableWithReduction</c> in the Java — and this type exists so
/// that branch can be read off rather than reasoned about.
/// </para>
///
/// <para>
/// The eight rows are not eight unrelated rates. They are four standardized
/// amounts, one per combination of the two payment-reduction flags, each split
/// two ways by the wage-index rule. FY2026: 6,752.61 with both quality data
/// submitted and no electronic-health-record reduction, 6,698.31 without the
/// quality data, 6,589.72 with the reduction, 6,535.43 with both. Knowing that
/// is useful for checking a loaded table; it is not a licence to compute one
/// row from another, for the rounding reason on
/// <see cref="IppsStandardizedAmount"/>.
/// </para>
/// </summary>
/// <param name="QualityDataSubmitted">
/// Whether the hospital met the inpatient quality reporting requirement. A
/// hospital that did not is paid a reduced market basket update, which means a
/// smaller standardized amount rather than a percentage taken off the
/// payment.
/// </param>
/// <param name="ElectronicRecordsReduction">
/// Whether the hospital is subject to the meaningful-use reduction for not
/// being a meaningful electronic health record user. Also a smaller
/// standardized amount, and independent of the quality flag: a hospital can
/// carry both.
/// </param>
/// <param name="WageIndexAboveOne">
/// Whether the area wage index is above 1.0, which chooses the labor split.
/// </param>
public readonly record struct IppsRateTableKey(
    bool QualityDataSubmitted,
    bool ElectronicRecordsReduction,
    bool WageIndexAboveOne)
{
    /// <summary>
    /// CMS's own number for this row, 1 to 8 — the <c>tab1</c> to <c>tab8</c>
    /// of its rate table file. Kept because it is how a loaded table can be
    /// read against the file it came from, which is otherwise a matter of
    /// trusting that the eight booleans were wired up the same way twice.
    /// </summary>
    public int CmsTableNumber =>
        (ElectronicRecordsReduction ? 4 : 0)
        + (WageIndexAboveOne ? 0 : 2)
        + (QualityDataSubmitted ? 1 : 2);

    /// <summary>The row that pays a stay at this hospital.</summary>
    public static IppsRateTableKey For(IppsProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return new IppsRateTableKey(
            provider.QualityDataSubmitted,
            provider.ElectronicRecordsReduction,
            WageIndexAboveOne: provider.WageIndex > 1m);
    }
}

/// <summary>
/// A fiscal year's federal operating rate table: eight standardized amounts,
/// keyed as CMS keys them.
///
/// <para>
/// Held as a lookup on CMS's own table numbers rather than as eight named
/// properties, because the file it is loaded from is keyed that way and a
/// loader that has to translate is a loader that can translate wrongly. All
/// eight must be present: a missing row is a half-loaded fiscal year, and the
/// alternative to failing is paying some hospitals from last year's rates
/// without saying so.
/// </para>
/// </summary>
public sealed class IppsOperatingRateTable
{
    private readonly Dictionary<int, IppsStandardizedAmount> rows;

    /// <summary>
    /// Creates the table from CMS's rows, keyed 1 to 8 as the rate table file
    /// keys them.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// When any of the eight rows is missing, or a key outside 1 to 8 is
    /// supplied.
    /// </exception>
    public IppsOperatingRateTable(
        IReadOnlyDictionary<int, IppsStandardizedAmount> amountsByCmsTableNumber)
    {
        ArgumentNullException.ThrowIfNull(amountsByCmsTableNumber);

        var unexpected = amountsByCmsTableNumber.Keys
            .Where(number => number is < 1 or > 8)
            .Order()
            .ToList();
        if (unexpected.Count > 0)
        {
            throw new ArgumentException(
                $"CMS's operating rate table is numbered 1 to 8; got {string.Join(", ", unexpected)}.",
                nameof(amountsByCmsTableNumber));
        }

        var missing = Enumerable.Range(1, 8)
            .Where(number => !amountsByCmsTableNumber.ContainsKey(number))
            .ToList();
        if (missing.Count > 0)
        {
            throw new ArgumentException(
                "A fiscal year's operating rate table has all eight of CMS's rows; "
                + $"{string.Join(", ", missing.Select(number => $"tab{number}"))} "
                + (missing.Count == 1 ? "is missing." : "are missing."),
                nameof(amountsByCmsTableNumber));
        }

        rows = new Dictionary<int, IppsStandardizedAmount>(amountsByCmsTableNumber);
    }

    /// <summary>The standardized amount for a row.</summary>
    public IppsStandardizedAmount For(IppsRateTableKey key) => rows[key.CmsTableNumber];
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
/// The update factors that carry a hospital's own historical base rate forward
/// to this fiscal year, for the hospital-specific portion of
/// 42 CFR 412.73 and 412.77.
///
/// <para>
/// Four factors keyed by the same two payment-reduction flags that choose a
/// row of <see cref="IppsOperatingRateTable"/>, and <b>not</b> by the wage
/// index: the hospital-specific portion is the hospital's own rate, so nothing
/// geographic touches it. A hospital that withheld its quality data or is not
/// a meaningful electronic health record user gets a smaller update here as
/// well as a smaller standardized amount, which is the same reduction reaching
/// the same discharge twice through two different numbers.
/// </para>
///
/// <para>
/// <b>The product is rounded to five decimal places, and that rounding is part
/// of the amount.</b> CMS rounds it there before multiplying, and the factor
/// multiplies a facility rate of several thousand dollars times a DRG weight,
/// so carrying the unrounded product through moves the payment by cents on
/// every sole-community discharge. It is not a display choice.
/// </para>
/// </summary>
/// <param name="WithQualityData">
/// The market basket update for a hospital that met the quality reporting
/// requirement and is a meaningful electronic health record user: 1.0260 for
/// FY2026.
/// </param>
/// <param name="WithQualityDataAndRecordsReduction">
/// Quality data submitted, but subject to the electronic health record
/// reduction: 1.00125.
/// </param>
/// <param name="WithoutQualityData">
/// No quality data, no records reduction: 1.01775.
/// </param>
/// <param name="WithoutQualityDataAndRecordsReduction">
/// Both reductions: 0.9930.
/// </param>
/// <param name="BudgetNeutrality">
/// The budget-neutrality factors CMS applies to the update, as one number.
/// For FY2026 it is two published factors multiplied together,
/// 0.998580 × 0.999897.
/// </param>
/// <param name="CumulativePriorYears">
/// The product of the prior years' updates, where this year's base rate is
/// still carried from an earlier rebasing, and 1.0 where it is not.
///
/// <para>
/// This is the field that cannot be guessed, and it is not a rounding detail:
/// through FY2025 CMS's pricer multiplied every update back to FY2019 into
/// this factor, and for FY2026 it is <b>1.0</b> — the chain restarts, and
/// FY2027 then carries FY2026's update alone. A year loaded with the previous
/// year's cumulative factor overpays sole-community hospitals by several
/// percent on every discharge, and does it quietly, because the payment still
/// looks like a payment.
/// </para>
/// </param>
public sealed record IppsHospitalSpecificUpdate(
    decimal WithQualityData,
    decimal WithQualityDataAndRecordsReduction,
    decimal WithoutQualityData,
    decimal WithoutQualityDataAndRecordsReduction,
    decimal BudgetNeutrality,
    decimal CumulativePriorYears)
{
    /// <summary>
    /// The factor this hospital's own base rate is carried forward by.
    /// </summary>
    public decimal FactorFor(IppsProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);

        var current = (provider.QualityDataSubmitted, provider.ElectronicRecordsReduction) switch
        {
            (true, false) => WithQualityData,
            (true, true) => WithQualityDataAndRecordsReduction,
            (false, false) => WithoutQualityData,
            (false, true) => WithoutQualityDataAndRecordsReduction
        };

        return decimal.Round(
            current * CumulativePriorYears * BudgetNeutrality, 5, MidpointRounding.AwayFromZero);
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
/// <param name="OperatingRates">
/// The eight-row federal operating rate table. This is what pays the DRG.
/// </param>
/// <param name="HigherLaborPercentages">
/// The labor and non-labor percentages CMS sets in the annual rule, which
/// apply where the wage index is above 1.0. Used by the outlier threshold, not
/// by the DRG payment — see <see cref="IppsLaborShare"/>.
/// </param>
/// <param name="StatutoryLaborPercentages">
/// The 62/38 percentages of 412.64(h)(3), which apply at or below 1.0.
/// </param>
/// <param name="CapitalFederalRate">The federal capital rate.</param>
/// <param name="Outlier">The cost outlier policy.</param>
/// <param name="Teaching">The indirect medical education formula.</param>
/// <param name="HospitalSpecificUpdate">
/// The update factors for the hospital-specific portion, which pays
/// sole-community, essential-access and Medicare-dependent hospitals their own
/// historical rate where it beats the federal one.
/// </param>
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
    IppsOperatingRateTable OperatingRates,
    IppsLaborShare HigherLaborPercentages,
    IppsLaborShare StatutoryLaborPercentages,
    decimal CapitalFederalRate,
    IppsOutlierPolicy Outlier,
    IppsTeachingPolicy Teaching,
    IppsHospitalSpecificUpdate HospitalSpecificUpdate,
    decimal EmpiricallyJustifiedDshShare)
{
    /// <summary>Whether these rates pay a stay discharged on the date.</summary>
    public bool Covers(DateOnly dischargeDate) =>
        dischargeDate >= FirstDischargeDate
        && (LastDischargeDate is null || dischargeDate <= LastDischargeDate);

    /// <summary>
    /// The standardized amount paying a stay at this hospital: the row of the
    /// operating rate table its two payment-reduction flags and its wage index
    /// select.
    /// </summary>
    public IppsStandardizedAmount StandardizedAmountFor(IppsProvider provider) =>
        OperatingRates.For(IppsRateTableKey.For(provider));

    /// <summary>
    /// The labor percentages for a wage index: the annual rule's above 1.0, and
    /// the statutory 62 percent at or below it.
    ///
    /// <para>
    /// The cost-of-living adjustment is deliberately not a parameter. It scales
    /// the non-labor side, so it does affect which pair pays more — but it does
    /// not affect which pair CMS chooses, and choosing the other one overpays.
    /// See the note on <see cref="IppsLaborShare"/>.
    /// </para>
    /// </summary>
    public IppsLaborShare PercentagesFor(decimal wageIndex) =>
        wageIndex > 1m ? HigherLaborPercentages : StatutoryLaborPercentages;
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
