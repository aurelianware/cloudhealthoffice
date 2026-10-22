using CloudHealthOffice.GrouperEngine.Ipps;
using Xunit;

namespace CloudHealthOffice.GrouperEngine.Tests;

/// <summary>
/// The IPPS payment arithmetic.
///
/// <para>
/// These are regulation tests, not parity tests. Each one names the paragraph
/// of 42 CFR it comes from and checks that this code does what the paragraph
/// says — which is a weaker claim than the grouper's, where the oracle is CMS's
/// own program. Until a PRICER parity run exists, that difference is the
/// honest statement of how much this is worth.
/// </para>
///
/// <para>
/// Every figure below is invented. The rates are round numbers chosen to make
/// the arithmetic checkable by hand, not any fiscal year's actual values, and
/// nothing here should be read as a published CMS rate.
/// </para>
/// </summary>
public class IppsPaymentCalculatorTests
{
    private const decimal StandardizedAmount = 6000m;
    private const decimal CapitalRate = 500m;

    /// <summary>
    /// A year whose two labor splits are 60/40 and 50/50, so that which one
    /// was chosen is visible in the answer.
    /// </summary>
    private static IppsRateSet Year(decimal fixedLoss = 30_000m) => new(
        FiscalYear: 2026,
        FirstDischargeDate: new DateOnly(2025, 10, 1),
        LastDischargeDate: new DateOnly(2026, 9, 30),
        OperatingStandardizedAmount: StandardizedAmount,
        HigherLaborShare: new IppsLaborShare(0.60m, 0.40m),
        StatutoryLaborShare: new IppsLaborShare(0.50m, 0.50m),
        CapitalFederalRate: CapitalRate,
        Outlier: new IppsOutlierPolicy(fixedLoss, 0.80m, 0.90m, new HashSet<int> { 927, 928 }),
        Teaching: new IppsTeachingPolicy(0.405d, 1.35m),
        EmpiricallyJustifiedDshShare: 0.25m);

    private static IppsStay Stay(
        decimal weight = 1m,
        IppsDischargeKind kind = IppsDischargeKind.Discharged,
        int los = 4,
        decimal geometricMean = 4m,
        decimal charges = 0m,
        int drg = 470,
        bool halfAndHalf = false) =>
        new(drg, weight, geometricMean, los, charges, kind, halfAndHalf);

    // ─────────────────────────────────────────────────────────
    //  The wage adjustment (42 CFR 412.64(h))
    // ─────────────────────────────────────────────────────────

    [Fact]
    public void A_wage_index_of_one_pays_the_standardized_amount_whichever_split_is_used()
    {
        // Both splits sum to 1.0, so at a wage index of 1.0 they cannot differ.
        // This is the test that would catch a labor share applied twice.
        var payment = IppsPaymentCalculator.Pay(
            Year(), IppsProvider.Plain("123456", wageIndex: 1m), Stay());

        Assert.Equal(StandardizedAmount, payment.Operating.WageAdjustedStandardizedAmount);
    }

    [Fact]
    public void Above_a_wage_index_of_one_the_larger_labor_share_pays_more_and_is_the_one_used()
    {
        var payment = IppsPaymentCalculator.Pay(
            Year(), IppsProvider.Plain("123456", wageIndex: 1.5m), Stay());

        // 412.64(h)(3) entitles the hospital to whichever share pays more, and
        // above 1.0 that is the larger labor share: 0.60 × 1.5 + 0.40 = 1.30.
        Assert.Equal(0.60m, payment.Operating.LaborRelatedShare);
        Assert.Equal(StandardizedAmount * 1.30m, payment.Operating.WageAdjustedStandardizedAmount);
    }

    [Fact]
    public void Below_a_wage_index_of_one_the_smaller_labor_share_pays_more_and_is_the_one_used()
    {
        var payment = IppsPaymentCalculator.Pay(
            Year(), IppsProvider.Plain("123456", wageIndex: 0.5m), Stay());

        // 0.50 × 0.5 + 0.50 = 0.75, against 0.60 × 0.5 + 0.40 = 0.70. The
        // regulation is written as the comparison, not as a test against 1.0,
        // and this is the direction a hard-coded comparison gets wrong.
        Assert.Equal(0.50m, payment.Operating.LaborRelatedShare);
        Assert.Equal(StandardizedAmount * 0.75m, payment.Operating.WageAdjustedStandardizedAmount);
    }

    [Fact]
    public void The_cost_of_living_adjustment_lifts_only_the_non_labor_share()
    {
        var provider = IppsProvider.Plain("123456", wageIndex: 1m) with
        {
            CostOfLivingAdjustment = 1.25m
        };

        var payment = IppsPaymentCalculator.Pay(Year(), provider, Stay());

        // 412.64(m)(1)(ii) adjusts the non-labor share and nothing else. Were
        // it reaching the labor share as well the multiplier would be 1.25 and
        // the answer 7,500.
        Assert.NotEqual(StandardizedAmount * 1.25m, payment.Operating.WageAdjustedStandardizedAmount);

        // And it is the SMALLER labor share that applies, which is not
        // obvious: at a wage index of 1.0 the two splits would pay the same,
        // but a cost-of-living adjustment above 1.0 rewards whichever split
        // leaves more in the non-labor share. 0.50 × 1.0 + 0.50 × 1.25 =
        // 1.125, against 0.60 × 1.0 + 0.40 × 1.25 = 1.10.
        //
        // That follows from the regulation rather than from a special case for
        // Alaska and Hawaii: 412.64(h)(3) entitles the hospital to the share
        // that pays it more, and nothing there limits the comparison to the
        // wage index. An implementation that chose the split by testing the
        // wage index against 1.0 would underpay every hospital in those two
        // states.
        Assert.Equal(0.50m, payment.Operating.LaborRelatedShare);
        Assert.Equal(StandardizedAmount * 1.125m, payment.Operating.WageAdjustedStandardizedAmount);
    }

    [Fact]
    public void The_drg_weight_multiplies_the_wage_adjusted_amount()
    {
        var payment = IppsPaymentCalculator.Pay(
            Year(), IppsProvider.Plain("123456", 1m), Stay(weight: 2.5m));

        Assert.Equal(StandardizedAmount * 2.5m, payment.Operating.BasePayment);
        Assert.Equal(CapitalRate * 2.5m, payment.Capital.BasePayment);
    }

    // ─────────────────────────────────────────────────────────
    //  Indirect medical education (42 CFR 412.105)
    // ─────────────────────────────────────────────────────────

    [Fact]
    public void A_hospital_with_no_residents_gets_no_teaching_payment()
    {
        var payment = IppsPaymentCalculator.Pay(
            Year(), IppsProvider.Plain("123456", 1m), Stay());

        Assert.Equal(0m, payment.Operating.TeachingAddOn);
    }

    [Fact]
    public void The_teaching_factor_is_the_three_steps_of_the_regulation()
    {
        // 412.105(d): ((1 + ratio) ^ 0.405 - 1) × 1.35. At a ratio of 1.0 that
        // is (2 ^ 0.405 - 1) × 1.35 = 0.32409... × 1.35 = 0.43752...
        var factor = new IppsTeachingPolicy(0.405d, 1.35m).FactorFor(1m);

        Assert.InRange(factor, 0.4375m, 0.4376m);
    }

    [Fact]
    public void The_teaching_payment_is_taken_on_the_base_payment_and_compounds_nothing()
    {
        var provider = IppsProvider.Plain("123456", 1m) with
        {
            ResidentToBedRatio = 1m,
            OperatingDshAdjustment = 0.20m
        };

        var payment = IppsPaymentCalculator.Pay(Year(), provider, Stay());

        // 412.105(a)(2) excludes outlier and 412.106 payments from the DRG
        // revenue the factor applies to, so a hospital with both add-ons must
        // not have one charged on the other.
        var expectedTeaching = payment.Operating.BasePayment
            * new IppsTeachingPolicy(0.405d, 1.35m).FactorFor(1m);

        Assert.Equal(expectedTeaching, payment.Operating.TeachingAddOn);
    }

    // ─────────────────────────────────────────────────────────
    //  Disproportionate share (42 CFR 412.106)
    // ─────────────────────────────────────────────────────────

    [Fact]
    public void Only_a_quarter_of_the_dsh_adjustment_is_paid_through_the_claim()
    {
        var provider = IppsProvider.Plain("123456", 1m) with { OperatingDshAdjustment = 0.20m };

        var payment = IppsPaymentCalculator.Pay(Year(), provider, Stay());

        // 412.106(f) reduces the 412.106(d) amount by 75 percent for
        // discharges on or after 1 October 2013. Paying the whole 20 percent
        // here would overpay every disproportionate share hospital fourfold on
        // this line.
        Assert.Equal(payment.Operating.BasePayment * 0.05m, payment.Operating.DisproportionateShareAddOn);
    }

    [Fact]
    public void Uncompensated_care_is_an_amount_per_discharge_and_does_not_scale_with_the_case()
    {
        var provider = IppsProvider.Plain("123456", 1m) with
        {
            UncompensatedCarePerDischarge = 900m
        };

        var light = IppsPaymentCalculator.Pay(Year(), provider, Stay(weight: 0.5m));
        var heavy = IppsPaymentCalculator.Pay(Year(), provider, Stay(weight: 5m));

        // 412.106(g) divides the hospital's annual allocation by its estimated
        // discharges, so the same amount is paid on a tonsillectomy and a
        // transplant.
        Assert.Equal(900m, light.Operating.UncompensatedCarePayment);
        Assert.Equal(900m, heavy.Operating.UncompensatedCarePayment);
    }

    // ─────────────────────────────────────────────────────────
    //  Transfers (42 CFR 412.4)
    // ─────────────────────────────────────────────────────────

    [Fact]
    public void A_discharge_is_paid_the_full_amount_and_has_no_transfer_reduction()
    {
        var payment = IppsPaymentCalculator.Pay(
            Year(), IppsProvider.Plain("123456", 1m), Stay());

        Assert.Null(payment.Transfer);
        Assert.Equal(payment.FullPayment, payment.PaymentBeforeOutliers);
    }

    [Fact]
    public void A_transfer_is_paid_twice_the_per_diem_for_the_first_day()
    {
        // 412.4(f)(1). A one-day transfer against a geometric mean of 10 is
        // paid two per diems, not one.
        var payment = IppsPaymentCalculator.Pay(
            Year(),
            IppsProvider.Plain("123456", 1m),
            Stay(kind: IppsDischargeKind.TransferredToAcuteCare, los: 1, geometricMean: 10m));

        var perDiem = payment.FullPayment / 10m;

        Assert.NotNull(payment.Transfer);
        Assert.Equal(2, payment.Transfer!.DaysPaid);
        Assert.Equal(perDiem, payment.Transfer.PerDiem);
        Assert.Equal(perDiem * 2m, payment.PaymentBeforeOutliers);
    }

    [Fact]
    public void A_transfer_never_pays_more_than_the_full_amount()
    {
        // A stay as long as the DRG's geometric mean already reaches the cap,
        // so the cap is the ordinary case and not an edge case.
        var payment = IppsPaymentCalculator.Pay(
            Year(),
            IppsProvider.Plain("123456", 1m),
            Stay(kind: IppsDischargeKind.TransferredToAcuteCare, los: 20, geometricMean: 4m));

        Assert.True(payment.Transfer!.CappedAtFullPayment);
        Assert.Equal(payment.FullPayment, payment.PaymentBeforeOutliers);
    }

    [Fact]
    public void A_post_acute_transfer_on_an_ordinary_drg_is_paid_the_same_per_diems()
    {
        var acute = IppsPaymentCalculator.Pay(
            Year(),
            IppsProvider.Plain("123456", 1m),
            Stay(kind: IppsDischargeKind.TransferredToAcuteCare, los: 1, geometricMean: 10m));

        var postAcute = IppsPaymentCalculator.Pay(
            Year(),
            IppsProvider.Plain("123456", 1m),
            Stay(kind: IppsDischargeKind.TransferredToPostAcuteCare, los: 1, geometricMean: 10m));

        // 412.4(f)(1) covers post-acute transfers too unless the DRG qualifies
        // for the half-and-half rule, so these must agree.
        Assert.Equal(acute.PaymentBeforeOutliers, postAcute.PaymentBeforeOutliers);
        Assert.False(postAcute.Transfer!.HalfAndHalf);
    }

    [Fact]
    public void A_post_acute_transfer_on_a_qualifying_drg_is_paid_half_the_full_amount_plus_half_the_per_diems()
    {
        var payment = IppsPaymentCalculator.Pay(
            Year(),
            IppsProvider.Plain("123456", 1m),
            Stay(
                kind: IppsDischargeKind.TransferredToPostAcuteCare,
                los: 1,
                geometricMean: 10m,
                halfAndHalf: true));

        // 412.4(f)(6): half the full rate for the first day plus half the
        // (f)(1) amount. Two per diems of a tenth each is a fifth of the full
        // payment, so this is 0.5 + 0.1 = 0.6 of it.
        Assert.True(payment.Transfer!.HalfAndHalf);
        Assert.Equal(payment.FullPayment * 0.6m, payment.PaymentBeforeOutliers);
    }

    [Fact]
    public void The_half_and_half_rule_does_not_apply_to_an_acute_transfer_even_on_a_qualifying_drg()
    {
        var payment = IppsPaymentCalculator.Pay(
            Year(),
            IppsProvider.Plain("123456", 1m),
            Stay(
                kind: IppsDischargeKind.TransferredToAcuteCare,
                los: 1,
                geometricMean: 10m,
                halfAndHalf: true));

        // The qualifying-DRG rules live in 412.4(c)'s post-acute paragraphs.
        Assert.False(payment.Transfer!.HalfAndHalf);
    }

    [Fact]
    public void A_transfer_on_a_drg_with_no_geometric_mean_stay_cannot_be_paid()
    {
        // There is no safe answer: treating it as a discharge overpays, and a
        // per diem of zero pays nothing. Both are silent, so this throws.
        var failure = Assert.Throws<IppsPaymentFailedException>(() =>
            IppsPaymentCalculator.Pay(
                Year(),
                IppsProvider.Plain("123456", 1m),
                Stay(kind: IppsDischargeKind.TransferredToAcuteCare, geometricMean: 0m)));

        Assert.Contains("geometric mean", failure.Message);
    }

    // ─────────────────────────────────────────────────────────
    //  Cost outliers (42 CFR 412.84)
    // ─────────────────────────────────────────────────────────

    [Fact]
    public void A_hospital_with_no_cost_to_charge_ratio_gets_no_outlier()
    {
        var payment = IppsPaymentCalculator.Pay(
            Year(), IppsProvider.Plain("123456", 1m), Stay(charges: 1_000_000m));

        Assert.Equal(0m, payment.Outlier.Payment);
    }

    [Fact]
    public void A_case_costing_less_than_the_threshold_gets_no_outlier()
    {
        var provider = WithRatios(0.30m, 0.05m);

        var payment = IppsPaymentCalculator.Pay(Year(), provider, Stay(charges: 10_000m));

        Assert.Equal(0m, payment.Outlier.Payment);
        Assert.Equal(3_500m, payment.Outlier.EstimatedCost);  // 10,000 × 0.35
    }

    [Fact]
    public void An_outlier_pays_eighty_percent_of_the_cost_above_the_threshold()
    {
        var provider = WithRatios(0.30m, 0.05m);
        var payment = IppsPaymentCalculator.Pay(Year(), provider, Stay(charges: 400_000m));

        // 412.84(k). Cost is 400,000 × 0.35 = 140,000; the threshold is the
        // case payment plus the 30,000 fixed loss amount, unadjusted here
        // because the wage index is 1.0.
        Assert.Equal(140_000m, payment.Outlier.EstimatedCost);
        Assert.Equal(payment.PaymentBeforeOutliers + 30_000m, payment.Outlier.Threshold);
        Assert.Equal(0.80m, payment.Outlier.MarginalCostFactor);
        Assert.Equal(0.80m * (140_000m - payment.Outlier.Threshold), payment.Outlier.Payment);
    }

    [Fact]
    public void A_burn_drg_outlier_pays_ninety_percent()
    {
        var provider = WithRatios(0.30m, 0.05m);
        var payment = IppsPaymentCalculator.Pay(
            Year(), provider, Stay(charges: 400_000m, drg: 927));

        // 412.84(l), for the burn DRGs the annual rate notice names.
        Assert.Equal(0.90m, payment.Outlier.MarginalCostFactor);
    }

    [Fact]
    public void Both_cost_to_charge_ratios_are_applied_because_a_hospitals_two_ratios_differ()
    {
        var operatingOnly = IppsPaymentCalculator.Pay(
            Year(), WithRatios(0.30m, 0m), Stay(charges: 100_000m));
        var both = IppsPaymentCalculator.Pay(
            Year(), WithRatios(0.30m, 0.05m), Stay(charges: 100_000m));

        // 412.84(g) keeps them separate; the costs they produce add.
        Assert.Equal(30_000m, operatingOnly.Outlier.EstimatedCost);
        Assert.Equal(35_000m, both.Outlier.EstimatedCost);
    }

    [Fact]
    public void The_fixed_loss_amount_is_wage_adjusted_like_the_standardized_amount()
    {
        var provider = WithRatios(0.30m, 0.05m) with { WageIndex = 1.5m };

        var payment = IppsPaymentCalculator.Pay(Year(), provider, Stay(charges: 400_000m));

        // The same 1.30 multiplier the standardized amount got, so the
        // threshold sits the same distance above the payment in a high-wage
        // area as in a low-wage one.
        Assert.Equal(payment.PaymentBeforeOutliers + (30_000m * 1.30m), payment.Outlier.Threshold);
    }

    [Fact]
    public void The_fixed_loss_amount_is_counted_once_and_not_once_per_side()
    {
        var provider = WithRatios(0.30m, 0.05m);
        var payment = IppsPaymentCalculator.Pay(Year(), provider, Stay(charges: 400_000m));

        // An earlier draft tested operating and capital separately against the
        // payment plus the full fixed loss amount each, which counts the
        // threshold twice and pays outliers on ordinary cases. The threshold
        // must exceed the payment by exactly one fixed loss amount.
        Assert.Equal(30_000m, payment.Outlier.Threshold - payment.PaymentBeforeOutliers);
    }

    [Fact]
    public void A_transfers_reduced_payment_lowers_the_outlier_threshold()
    {
        var provider = WithRatios(0.30m, 0.05m);

        var discharged = IppsPaymentCalculator.Pay(Year(), provider, Stay(charges: 400_000m));
        var transferred = IppsPaymentCalculator.Pay(
            Year(),
            provider,
            Stay(
                charges: 400_000m,
                kind: IppsDischargeKind.TransferredToAcuteCare,
                los: 1,
                geometricMean: 10m));

        // 412.4(f)(4) allows an outlier on a transfer, and the threshold is
        // measured from what the case is paid.
        Assert.True(transferred.Outlier.Threshold < discharged.Outlier.Threshold);
        Assert.True(transferred.Outlier.Payment > discharged.Outlier.Payment);
    }

    // ─────────────────────────────────────────────────────────
    //  The total, and the fiscal year
    // ─────────────────────────────────────────────────────────

    [Fact]
    public void The_total_is_the_case_payment_plus_the_outlier()
    {
        var provider = WithRatios(0.30m, 0.05m);
        var payment = IppsPaymentCalculator.Pay(Year(), provider, Stay(charges: 400_000m));

        Assert.Equal(payment.PaymentBeforeOutliers + payment.Outlier.Payment, payment.Total);
    }

    [Fact]
    public void Every_line_of_the_payment_is_reported_so_a_dispute_can_name_one()
    {
        var provider = WithRatios(0.30m, 0.05m) with
        {
            ResidentToBedRatio = 0.5m,
            OperatingDshAdjustment = 0.20m,
            UncompensatedCarePerDischarge = 900m,
            CapitalIndirectMedicalEducationFactor = 0.05m,
            CapitalDisproportionateShareFactor = 0.03m
        };

        var payment = IppsPaymentCalculator.Pay(Year(), provider, Stay(weight: 2m));

        Assert.True(payment.Operating.TeachingAddOn > 0);
        Assert.True(payment.Operating.DisproportionateShareAddOn > 0);
        Assert.Equal(900m, payment.Operating.UncompensatedCarePayment);
        Assert.True(payment.Capital.TeachingAddOn > 0);
        Assert.True(payment.Capital.DisproportionateShareAddOn > 0);

        // The sum of the parts is the whole, which is the property an audit
        // trail has to have to be worth keeping.
        Assert.Equal(payment.Operating.Total + payment.Capital.Total, payment.FullPayment);
    }

    [Fact]
    public void Rates_pay_the_discharge_dates_in_their_own_fiscal_year_and_no_others()
    {
        var year = Year();

        Assert.False(year.Covers(new DateOnly(2025, 9, 30)));
        Assert.True(year.Covers(new DateOnly(2025, 10, 1)));
        Assert.True(year.Covers(new DateOnly(2026, 9, 30)));
        Assert.False(year.Covers(new DateOnly(2026, 10, 1)));
    }

    [Fact]
    public async Task A_stay_is_paid_by_the_rates_in_force_on_its_discharge_date()
    {
        var provider = new IppsRateYears(
        [
            Year() with { FiscalYear = 2025, FirstDischargeDate = new DateOnly(2024, 10, 1), LastDischargeDate = new DateOnly(2025, 9, 30) },
            Year()
        ]);

        var found = await provider.GetForDischargeDateAsync(new DateOnly(2026, 3, 4));

        Assert.Equal(2026, found!.FiscalYear);
    }

    [Fact]
    public async Task A_discharge_date_no_loaded_year_covers_has_no_rates()
    {
        var provider = new IppsRateYears([Year()]);

        Assert.Null(await provider.GetForDischargeDateAsync(new DateOnly(2030, 1, 1)));
    }

    private static IppsProvider WithRatios(decimal operating, decimal capital) =>
        IppsProvider.Plain("123456", wageIndex: 1m) with
        {
            OperatingCostToChargeRatio = operating,
            CapitalCostToChargeRatio = capital
        };
}
