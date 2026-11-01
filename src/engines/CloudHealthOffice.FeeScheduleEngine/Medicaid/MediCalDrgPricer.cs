using System.Globalization;
using CloudHealthOffice.Infrastructure.Money;

namespace CloudHealthOffice.FeeScheduleEngine.Medicaid;

/// <summary>
/// A Medi-Cal policy adjustor: a multiplier on the payment weight for a care
/// category, optionally limited to a severity range and to designated NICU
/// hospitals (or to all other hospitals). Published each state fiscal year
/// with the DRG pricing calculator.
/// </summary>
/// <param name="CareCategory">The care category it applies to, matched case-insensitively.</param>
/// <param name="Factor">The multiplier, e.g. 1.85 for neonate at a designated NICU (SFY 2026-27).</param>
/// <param name="MinSoi">Lowest severity it applies to (default 1).</param>
/// <param name="MaxSoi">Highest severity it applies to (default 4).</param>
/// <param name="DesignatedNicu">True: only designated NICU hospitals. False: only other hospitals. Null: both.</param>
public sealed record MediCalPolicyAdjustor(
    string CareCategory,
    decimal Factor,
    int MinSoi = 1,
    int MaxSoi = 4,
    bool? DesignatedNicu = null);

/// <summary>
/// A Medi-Cal age adjustor: a multiplier for a care category when the
/// patient is under an age at admission (e.g. 1.25 for the pediatric
/// respiratory and pediatric miscellaneous categories under 21).
/// </summary>
public sealed record MediCalAgeAdjustor(string CareCategory, decimal Factor, int UnderAge = 21);

/// <summary>
/// One tier of the Medi-Cal cost outlier: loss above <see cref="Threshold"/>
/// (and below the next tier's threshold) is paid at <see cref="MarginalPercent"/>.
/// </summary>
public sealed record MediCalOutlierTier(decimal Threshold, decimal MarginalPercent);

/// <summary>
/// Medi-Cal's state-wide DRG parameters for one state fiscal year. DHCS
/// publishes them with the DRG pricing calculator; none has a default here,
/// because a wrong default would pay every stay wrongly.
/// </summary>
public sealed record MediCalDrgPolicy
{
    /// <summary>
    /// Cost outlier tiers, lowest threshold first. SFY 2026-27 has one tier at
    /// a $99,000 threshold. A stay with a loss above the first threshold cannot
    /// be priced while this is empty.
    /// </summary>
    public IReadOnlyList<MediCalOutlierTier> CostOutlierTiers { get; init; } = [];

    /// <summary>
    /// Optional low-side outlier: when the DRG payment exceeds estimated cost
    /// by more than the threshold, the excess is reduced at the marginal
    /// percent. Off unless configured.
    /// </summary>
    public MediCalOutlierTier? LowSideOutlier { get; init; }

    /// <summary>Policy adjustors. The first matching rule applies; list NICU-specific rules first.</summary>
    public IReadOnlyList<MediCalPolicyAdjustor> PolicyAdjustors { get; init; } = [];

    /// <summary>Age adjustors, applied on top of the policy adjustor.</summary>
    public IReadOnlyList<MediCalAgeAdjustor> AgeAdjustors { get; init; } = [];

    /// <summary>"Payment cannot exceed charges" (DRG payment method, section 4.4).</summary>
    public bool CapAtCharges { get; init; } = true;

    /// <summary>
    /// Discharge statuses paid as an acute transfer: Medicare's acute list
    /// plus 65 (psychiatric hospital or unit), which Medi-Cal adds.
    /// </summary>
    public IReadOnlySet<string> TransferStatuses { get; init; } =
        new HashSet<string>(DischargeStatuses.MedicareAcuteTransfer, StringComparer.Ordinal) { "65" };
}

/// <summary>
/// Prices a Medi-Cal inpatient stay under the DRG payment method (State Plan
/// Attachment 4.19-A):
///
/// <list type="number">
/// <item>Base price, wage adjusted: base × (labor share × wage index + (1 − labor share)),
/// except a hospital-specific transitional base price, which is not wage adjusted.</item>
/// <item>Payment weight = relative weight × policy adjustor × age adjustor.</item>
/// <item>DRG payment = adjusted base × payment weight.</item>
/// <item>Acute transfer: per diem (DRG payment ÷ ALOS) × (days + 1), capped at the DRG payment.</item>
/// <item>Cost outlier: estimated cost = charges × CCR; loss = cost − DRG payment;
/// the loss above each tier's threshold is paid at that tier's marginal percent.</item>
/// <item>Allowed = DRG payment + outlier, never more than charges.</item>
/// </list>
/// </summary>
public sealed class MediCalDrgPricer(MediCalDrgPolicy policy) : IMedicaidDrgPricer
{
    private readonly MediCalDrgPolicy _policy = policy ?? throw new ArgumentNullException(nameof(policy));

    /// <inheritdoc />
    public MedicaidDrgPrice Price(MedicaidDrgStay stay, MedicaidDrgWeight? weight, MedicaidDrgHospital hospital)
    {
        ArgumentNullException.ThrowIfNull(stay);
        ArgumentNullException.ThrowIfNull(hospital);

        if (weight is null)
            return MedicaidDrgPrice.Unpriced($"No Medi-Cal APR-DRG relative weight is loaded for {stay.DrgSoi}.");
        if (weight.RelativeWeight <= 0)
            return MedicaidDrgPrice.Unpriced($"The relative weight for {stay.DrgSoi} is not positive.");
        if (hospital.BaseRate <= 0)
            return MedicaidDrgPrice.Unpriced($"No Medi-Cal base price is loaded for hospital {hospital.Npi}.");
        if (hospital.CostToChargeRatio is not { } ccr || ccr <= 0)
            return MedicaidDrgPrice.Unpriced($"No cost-to-charge ratio is loaded for hospital {hospital.Npi}; the cost outlier cannot be evaluated.");
        if (Soi(stay.DrgSoi) is not { } soi)
            return MedicaidDrgPrice.Unpriced($"'{stay.DrgSoi}' is not a DRG-SOI key.");

        var steps = new List<string>();

        // 1. Wage-adjusted base price.
        var basePrice = hospital.BaseRate;
        if (hospital.BaseRateIsTransitional || hospital.WageIndex is null)
        {
            steps.Add(hospital.BaseRateIsTransitional
                ? $"Base price {Money(basePrice)} is hospital-specific transitional; not wage adjusted."
                : $"Base price {Money(basePrice)}; no wage index on file, so not wage adjusted.");
        }
        else
        {
            if (hospital.LaborShare is not { } labor || labor is <= 0 or >= 1)
                return MedicaidDrgPrice.Unpriced($"Hospital {hospital.Npi} has a wage index but no labor share between 0 and 1.");

            var index = hospital.WageIndex.Value;
            var factor = labor * index + (1 - labor);
            var adjusted = basePrice * factor;
            steps.Add($"Wage-adjusted base = {Money(basePrice)} × ({labor} × {index} + {1 - labor}) = {Money(adjusted)}.");
            basePrice = adjusted;
        }

        // 2. Payment weight.
        var policyFactor = PolicyAdjustor(weight.CareCategory, soi, hospital.IsDesignatedNicu);
        var ageFactor = AgeAdjustor(weight.CareCategory, stay.AgeYearsAtAdmission, out var ageUnknown);
        if (ageUnknown)
            return MedicaidDrgPrice.Unpriced($"An age adjustor applies to care category '{weight.CareCategory}' and the age at admission is unknown.");

        var paymentWeight = weight.RelativeWeight * policyFactor * ageFactor;
        steps.Add($"Payment weight = relative weight {weight.RelativeWeight} ({stay.DrgSoi}, {weight.CareCategory ?? "no care category"}) "
            + $"× policy adjustor {policyFactor} × age adjustor {ageFactor} = {paymentWeight}.");

        // 3. DRG payment.
        var full = basePrice * paymentWeight;
        steps.Add($"DRG payment = {Money(basePrice)} × {paymentWeight} = {Money(full)}.");

        // 4. Transfer.
        var drgPayment = full;
        var transfer = _policy.TransferStatuses.Contains(DischargeStatuses.Normalize(stay.DischargeStatus) ?? "");
        if (transfer)
        {
            if (weight.MeanLengthOfStay is not { } alos || alos <= 0)
                return MedicaidDrgPrice.Unpriced($"The stay is a transfer and no ALOS is loaded for {stay.DrgSoi}.");

            var perDiem = full / alos;
            var byDays = perDiem * (stay.LengthOfStayDays + 1);
            drgPayment = Math.Min(full, byDays);
            steps.Add($"Transfer (status {stay.DischargeStatus}): per diem {Money(perDiem)} (÷ ALOS {alos}) × ({stay.LengthOfStayDays} + 1) day(s) = {Money(byDays)}, "
                + $"capped at the DRG payment = {Money(drgPayment)}.");
        }

        // 5. Outliers.
        var cost = stay.AllowedCharges * ccr;
        var loss = cost - drgPayment;
        steps.Add($"Estimated cost = charges {Money(stay.AllowedCharges)} × CCR {ccr} = {Money(cost)}; loss = {Money(loss)}.");

        var outlier = 0m;
        var kind = MedicaidOutlierKind.None;
        var tiers = _policy.CostOutlierTiers.OrderBy(t => t.Threshold).ToList();
        if (loss > 0 && (tiers.Count == 0 || loss > tiers[0].Threshold))
        {
            if (tiers.Count == 0)
                return MedicaidDrgPrice.Unpriced("Medi-Cal cost outlier thresholds are not configured for this fiscal year.");

            for (var i = 0; i < tiers.Count; i++)
            {
                var floor = tiers[i].Threshold;
                if (loss <= floor) break;
                var ceiling = i + 1 < tiers.Count ? Math.Min(loss, tiers[i + 1].Threshold) : loss;
                var piece = (ceiling - floor) * tiers[i].MarginalPercent;
                outlier += piece;
                steps.Add($"Cost outlier tier {i + 1}: ({Money(ceiling)} − {Money(floor)}) × {Pct(tiers[i].MarginalPercent)} = {Money(piece)}.");
            }
            kind = MedicaidOutlierKind.CostOutlier;
        }
        else if (loss < 0 && _policy.LowSideOutlier is { } low && -loss > low.Threshold)
        {
            outlier = -((-loss - low.Threshold) * low.MarginalPercent);
            kind = MedicaidOutlierKind.LowSideCostOutlier;
            steps.Add($"Low-side outlier: (gain {Money(-loss)} − {Money(low.Threshold)}) × {Pct(low.MarginalPercent)} = {Money(outlier)}.");
        }
        else
        {
            steps.Add("No outlier.");
        }

        // 6. Charge cap.
        var total = drgPayment + outlier;
        var capped = false;
        if (_policy.CapAtCharges && total > stay.AllowedCharges)
        {
            steps.Add($"Payment {Money(total)} exceeds charges; reduced to {Money(stay.AllowedCharges)}.");
            total = stay.AllowedCharges;
            capped = true;
        }

        steps.Add($"Allowed = {Money(total)}.");

        return new MedicaidDrgPrice
        {
            Priced = true,
            AdjustedBaseRate = MoneyRounding.ToCents(basePrice),
            PaymentWeight = paymentWeight,
            FullDrgPayment = MoneyRounding.ToCents(full),
            DrgPayment = MoneyRounding.ToCents(drgPayment),
            TransferAdjusted = transfer,
            OutlierPayment = MoneyRounding.ToCents(outlier),
            OutlierKind = kind,
            ChargeCapApplied = capped,
            AllowedAmount = MoneyRounding.ToCents(total),
            Steps = steps,
        };
    }

    private decimal PolicyAdjustor(string? careCategory, int soi, bool designatedNicu)
    {
        if (string.IsNullOrWhiteSpace(careCategory)) return 1m;
        var rule = _policy.PolicyAdjustors.FirstOrDefault(r =>
            string.Equals(r.CareCategory, careCategory.Trim(), StringComparison.OrdinalIgnoreCase)
            && soi >= r.MinSoi && soi <= r.MaxSoi
            && (r.DesignatedNicu is null || r.DesignatedNicu == designatedNicu));
        return rule?.Factor ?? 1m;
    }

    private decimal AgeAdjustor(string? careCategory, int? age, out bool ageUnknown)
    {
        ageUnknown = false;
        if (string.IsNullOrWhiteSpace(careCategory)) return 1m;
        var rule = _policy.AgeAdjustors.FirstOrDefault(r =>
            string.Equals(r.CareCategory, careCategory.Trim(), StringComparison.OrdinalIgnoreCase));
        if (rule is null) return 1m;
        if (age is null)
        {
            ageUnknown = true;
            return 1m;
        }
        return age < rule.UnderAge ? rule.Factor : 1m;
    }

    internal static int? Soi(string drgSoi)
    {
        var dash = drgSoi.LastIndexOf('-');
        return dash > 0 && int.TryParse(drgSoi[(dash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var soi)
               && soi is >= 1 and <= 4
            ? soi
            : null;
    }

    private static string Money(decimal amount) => MoneyRounding.ToCents(amount).ToString("C", CultureInfo.GetCultureInfo("en-US"));

    private static string Pct(decimal fraction) => (fraction * 100m).ToString("0.##", CultureInfo.InvariantCulture) + "%";
}
