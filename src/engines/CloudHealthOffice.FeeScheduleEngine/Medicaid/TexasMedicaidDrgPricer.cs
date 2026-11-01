using System.Globalization;
using CloudHealthOffice.Infrastructure.Money;

namespace CloudHealthOffice.FeeScheduleEngine.Medicaid;

/// <summary>
/// Texas Medicaid's state-wide parameters for APR-DRG inpatient pricing,
/// 1 TAC §355.8052. Defaults are the values the rule states; anything HHSC
/// republishes each rate year (the universal mean) has no default.
/// </summary>
public sealed record TexasDrgPolicy
{
    /// <summary>
    /// The universal mean (average cost per claim, §355.8052(b)), in dollars.
    /// Needed for the cost outlier threshold.
    /// </summary>
    public decimal? UniversalMean { get; init; }

    /// <summary>Multiplier on the lesser of universal mean and SDA for the cost outlier threshold (§355.8052(i)(4)(B)).</summary>
    public decimal CostOutlierThresholdFactor { get; init; } = 11.14m;

    /// <summary>Multiple of the full DRG payment that is the alternative cost outlier threshold.</summary>
    public decimal CostOutlierDrgMultiple { get; init; } = 1.5m;

    /// <summary>Share of cost above the threshold paid as a cost outlier.</summary>
    public decimal CostOutlierMarginalPercent { get; init; } = 0.60m;

    /// <summary>Share of the DRG per diem paid for each day above the day outlier threshold.</summary>
    public decimal DayOutlierPerDiemPercent { get; init; } = 0.60m;

    /// <summary>Days beyond the MLOS a stay must exceed before it can be a day outlier.</summary>
    public int DayOutlierMinimumDaysOverMlos { get; init; } = 2;

    /// <summary>Share of a computed outlier paid to urban and rural hospitals; children's hospitals get 100%.</summary>
    public decimal NonChildrensOutlierShare { get; init; } = 0.90m;

    /// <summary>Outliers are paid only for clients under this age.</summary>
    public int OutlierAgeLimit { get; init; } = 21;

    /// <summary>For transferring patients this age or older the transfer per diem is capped at this many days.</summary>
    public int AdultTransferDayCap { get; init; } = 30;

    /// <summary>
    /// Discharge statuses that are a hospital-to-hospital transfer. Defaults
    /// to Medicare's acute-transfer list; §355.8052(i)(6) does not enumerate
    /// codes, so a plan should confirm against its HHSC contract.
    /// </summary>
    public IReadOnlySet<string> TransferStatuses { get; init; } = DischargeStatuses.MedicareAcuteTransfer;

    /// <summary>
    /// Base APR-DRGs that are labor and delivery stays, which a rural
    /// hospital is paid at its OB-GYN delivery SDA (§355.8052(e)(2)). The
    /// rule names services, not DRGs: the default is the APR-DRG delivery
    /// groups (540 cesarean, 541 vaginal with sterilization or D&amp;C, 542
    /// vaginal with complicating procedures, 560 vaginal). Confirm against
    /// the plan's HHSC guidance.
    /// </summary>
    public IReadOnlySet<string> DeliveryBaseDrgs { get; init; } = new HashSet<string>(StringComparer.Ordinal) { "540", "541", "542", "560" };
}

/// <summary>
/// Prices a Texas Medicaid inpatient stay, 1 TAC §355.8052(i):
///
/// <list type="number">
/// <item>DRG payment = hospital final SDA × relative weight of the DRG-SOI.</item>
/// <item>A hospital-to-hospital transfer pays the transferring hospital a
/// per diem (weight × SDA ÷ MLOS) × the lesser of MLOS, allowed days, and
/// 30 days for patients 21 or older.</item>
/// <item>For clients under 21, a day outlier and a cost outlier are both
/// computed and the higher positive one is paid, at 90% (urban and rural) or
/// 100% (children's hospitals).</item>
/// </list>
///
/// Payment is not capped at charges: the rule describes the prospective
/// payment as full payment and denies any request above it.
/// </summary>
public sealed class TexasMedicaidDrgPricer(TexasDrgPolicy policy) : IMedicaidDrgPricer
{
    private readonly TexasDrgPolicy _policy = policy ?? throw new ArgumentNullException(nameof(policy));

    /// <inheritdoc />
    public MedicaidDrgPrice Price(MedicaidDrgStay stay, MedicaidDrgWeight? weight, MedicaidDrgHospital hospital)
    {
        ArgumentNullException.ThrowIfNull(stay);
        ArgumentNullException.ThrowIfNull(hospital);

        if (weight is null)
            return MedicaidDrgPrice.Unpriced($"No Texas APR-DRG relative weight is loaded for {stay.DrgSoi}.");
        if (weight.RelativeWeight <= 0)
            return MedicaidDrgPrice.Unpriced($"The relative weight for {stay.DrgSoi} is not positive.");
        if (hospital.BaseRate <= 0)
            return MedicaidDrgPrice.Unpriced($"No Texas SDA is loaded for hospital {hospital.Npi}.");

        var steps = new List<string>();
        var sda = hospital.BaseRate;
        var sdaLabel = "SDA";
        var baseDrg = stay.DrgSoi.Split('-', 2)[0].Trim();
        if (hospital.DeliveryBaseRate is { } deliverySda && deliverySda > 0 && _policy.DeliveryBaseDrgs.Contains(baseDrg))
        {
            sda = deliverySda;
            sdaLabel = "OB-GYN delivery SDA";
            steps.Add($"APR-DRG {baseDrg} is a labor and delivery stay; the hospital's delivery SDA applies (§355.8052(e)(2)).");
        }
        var full = sda * weight.RelativeWeight;
        steps.Add($"DRG payment = {sdaLabel} {Money(sda)} × relative weight {weight.RelativeWeight} ({stay.DrgSoi}) = {Money(full)}.");

        var drgPayment = full;
        var transfer = _policy.TransferStatuses.Contains(DischargeStatuses.Normalize(stay.DischargeStatus) ?? "");
        if (transfer)
        {
            if (weight.MeanLengthOfStay is not { } mlos || mlos <= 0)
                return MedicaidDrgPrice.Unpriced($"The stay is a transfer and no MLOS is loaded for {stay.DrgSoi}.");

            var adult = stay.AgeYearsAtAdmission is { } age && age >= _policy.OutlierAgeLimit;
            var days = Math.Min(mlos, stay.LengthOfStayDays);
            if (adult) days = Math.Min(days, _policy.AdultTransferDayCap);
            if (stay.AgeYearsAtAdmission is null)
            {
                // The 30-day cap depends on age; an unknown age takes the
                // lower payment rather than the higher.
                days = Math.Min(days, _policy.AdultTransferDayCap);
                steps.Add("Age at admission unknown; the adult 30-day transfer cap was applied.");
            }

            var perDiem = full / mlos;
            drgPayment = perDiem * days;
            steps.Add($"Transfer (status {stay.DischargeStatus}): per diem {Money(perDiem)} (DRG payment ÷ MLOS {mlos}) × {days} day(s) = {Money(drgPayment)}.");
        }

        var (outlier, kind) = Outlier(stay, weight, hospital, full, drgPayment, steps);
        if (outlier is null)
        {
            return MedicaidDrgPrice.Unpriced(steps[^1]);
        }

        var total = drgPayment + outlier.Value;
        steps.Add($"Allowed = DRG payment {Money(drgPayment)} + outlier {Money(outlier.Value)} = {Money(total)}.");

        return new MedicaidDrgPrice
        {
            Priced = true,
            AdjustedBaseRate = sda,
            PaymentWeight = weight.RelativeWeight,
            FullDrgPayment = MoneyRounding.ToCents(full),
            DrgPayment = MoneyRounding.ToCents(drgPayment),
            TransferAdjusted = transfer,
            OutlierPayment = MoneyRounding.ToCents(outlier.Value),
            OutlierKind = kind,
            AllowedAmount = MoneyRounding.ToCents(total),
            Steps = steps,
        };
    }

    /// <summary>
    /// The outlier for a client under 21, or 0. Null (with the reason as the
    /// last step) when an outlier may be due but cannot be computed: paying
    /// only the DRG amount there would underpay a children's stay silently.
    /// </summary>
    private (decimal? Amount, MedicaidOutlierKind Kind) Outlier(
        MedicaidDrgStay stay, MedicaidDrgWeight weight, MedicaidDrgHospital hospital,
        decimal fullDrgPayment, decimal drgPayment, List<string> steps)
    {
        if (stay.AgeYearsAtAdmission is not { } age)
        {
            steps.Add("Age at admission unknown; Texas outliers are only for clients under 21, so the stay cannot be priced.");
            return (null, MedicaidOutlierKind.None);
        }

        if (age >= _policy.OutlierAgeLimit)
        {
            steps.Add($"No outlier: Texas outliers are for clients under {_policy.OutlierAgeLimit}.");
            return (0m, MedicaidOutlierKind.None);
        }

        if (hospital.CostToChargeRatio is not { } interimRate || interimRate <= 0)
        {
            steps.Add($"The client is under {_policy.OutlierAgeLimit} and no interim rate is loaded for hospital {hospital.Npi}; outliers cannot be evaluated.");
            return (null, MedicaidOutlierKind.None);
        }

        var share = hospital.TexasClass == TexasHospitalClass.Childrens ? 1m : _policy.NonChildrensOutlierShare;
        var cost = stay.AllowedCharges * interimRate;

        // Day outlier, §355.8052(i)(4)(A).
        var dayOutlier = 0m;
        if (weight.MeanLengthOfStay is { } mlos && mlos > 0 && weight.DayOutlierThreshold is { } threshold)
        {
            if (stay.LengthOfStayDays > mlos + _policy.DayOutlierMinimumDaysOverMlos && stay.LengthOfStayDays > threshold)
            {
                var perDiem = fullDrgPayment / mlos;
                var daysOver = stay.LengthOfStayDays - threshold;
                var byDays = daysOver * perDiem * _policy.DayOutlierPerDiemPercent;
                var byCost = cost - drgPayment;
                dayOutlier = Math.Max(0m, Math.Min(byDays, byCost)) * share;
                steps.Add($"Day outlier: lesser of {daysOver} day(s) over threshold {threshold} × {Pct(_policy.DayOutlierPerDiemPercent)} of per diem {Money(perDiem)} = {Money(byDays)} "
                    + $"and cost {Money(cost)} − DRG payment {Money(drgPayment)} = {Money(byCost)}, × {Pct(share)} = {Money(dayOutlier)}.");
            }
        }
        else
        {
            steps.Add($"No day outlier statistics are loaded for {stay.DrgSoi}; only the cost outlier was evaluated.");
        }

        // Cost outlier, §355.8052(i)(4)(B).
        if (_policy.UniversalMean is not { } universalMean || universalMean <= 0)
        {
            steps.Add("The Texas universal mean is not configured; the cost outlier cannot be evaluated.");
            return (null, MedicaidOutlierKind.None);
        }

        var costThreshold = Math.Max(
            Math.Min(universalMean, hospital.BaseRate) * _policy.CostOutlierThresholdFactor,
            _policy.CostOutlierDrgMultiple * fullDrgPayment);
        var costOutlier = Math.Max(0m, (cost - costThreshold) * _policy.CostOutlierMarginalPercent) * share;
        steps.Add($"Cost outlier: threshold {Money(costThreshold)} (greater of {_policy.CostOutlierThresholdFactor} × lesser of universal mean and SDA, "
            + $"and {_policy.CostOutlierDrgMultiple} × DRG payment); cost {Money(cost)} (charges × interim rate {interimRate}); "
            + $"{Pct(_policy.CostOutlierMarginalPercent)} of excess × {Pct(share)} = {Money(costOutlier)}.");

        if (dayOutlier <= 0 && costOutlier <= 0)
        {
            steps.Add("Neither outlier is positive; none is paid.");
            return (0m, MedicaidOutlierKind.None);
        }

        return costOutlier >= dayOutlier
            ? (costOutlier, MedicaidOutlierKind.CostOutlier)
            : (dayOutlier, MedicaidOutlierKind.DayOutlier);
    }

    private static string Money(decimal amount) => MoneyRounding.ToCents(amount).ToString("C", CultureInfo.GetCultureInfo("en-US"));

    private static string Pct(decimal fraction) => (fraction * 100m).ToString("0.##", CultureInfo.InvariantCulture) + "%";
}
