using CloudHealthOffice.GrouperEngine.Ipps;
using Xunit;

namespace CloudHealthOffice.GrouperEngine.Tests;

/// <summary>
/// The IPPS payment arithmetic.
///
/// <para>
/// These are regulation tests, not parity tests. Each one names the paragraph
/// of 42 CFR it comes from and checks that this code does what the paragraph
/// says. <c>IppsPricerParityTests</c> is the other half and the stronger
/// claim: it holds the same code to the amounts CMS's own pricer paid. Both
/// are needed — the parity run reversed a rule these tests had agreed with,
/// and these tests catch the arithmetic the corpus does not happen to reach.
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
    /// An operating rate table whose eight rows are all distinguishable by
    /// eye, so that a test can say which one was used rather than only that
    /// the arithmetic was consistent.
    ///
    /// <para>
    /// Four base amounts, one per combination of the two payment reductions —
    /// 6,000 with neither, 5,000 without the quality data, 4,000 with the
    /// electronic-health-record reduction, 3,000 with both — each split 60/40
    /// above a wage index of 1.0 and 50/50 at or below it. The splits match
    /// the percentage pair below so that the outlier tests stay checkable by
    /// hand.
    /// </para>
    /// </summary>
    private static IppsOperatingRateTable RateTable() => new(new Dictionary<int, IppsStandardizedAmount>
    {
        [1] = new(3600m, 2400m),  // quality data, no reduction, wage index above 1.0
        [2] = new(3000m, 2000m),  // no quality data, no reduction, above 1.0
        [3] = new(3000m, 3000m),  // quality data, no reduction, at or below 1.0
        [4] = new(2500m, 2500m),  // no quality data, no reduction, at or below 1.0
        [5] = new(2400m, 1600m),  // quality data, record reduction, above 1.0
        [6] = new(1800m, 1200m),  // no quality data, record reduction, above 1.0
        [7] = new(2000m, 2000m),  // quality data, record reduction, at or below 1.0
        [8] = new(1500m, 1500m)   // no quality data, record reduction, at or below 1.0
    });

    /// <summary>
    /// A year whose two labor percentage pairs are 60/40 and 50/50, so that
    /// which one was chosen is visible in the answer.
    /// </summary>
    private static IppsRateSet Year(decimal fixedLoss = 30_000m) => new(
        FiscalYear: 2026,
        FirstDischargeDate: new DateOnly(2025, 10, 1),
        LastDischargeDate: new DateOnly(2026, 9, 30),
        OperatingRates: RateTable(),
        HigherLaborPercentages: new IppsLaborShare(0.60m, 0.40m),
        StatutoryLaborPercentages: new IppsLaborShare(0.50m, 0.50m),
        CapitalFederalRate: CapitalRate,
        Outlier: new IppsOutlierPolicy(fixedLoss, 0.80m, 0.90m, new HashSet<int> { 927, 928 }),
        Teaching: new IppsTeachingPolicy(0.405d, 1.35m),
        HospitalSpecificUpdate: HospitalSpecificUpdate(),
        EmpiricallyJustifiedDshShare: 0.25m);

    /// <summary>
    /// Hospital-specific update factors chosen so that a facility rate passes
    /// through unchanged in the common case: 1.0 for a hospital with its
    /// quality data in and no record reduction, and visibly smaller for each
    /// other combination. The product of the three published factors is pinned
    /// separately, below.
    /// </summary>
    private static IppsHospitalSpecificUpdate HospitalSpecificUpdate() => new(
        WithQualityData: 1m,
        WithQualityDataAndRecordsReduction: 0.9m,
        WithoutQualityData: 0.95m,
        WithoutQualityDataAndRecordsReduction: 0.85m,
        BudgetNeutrality: 1m,
        CumulativePriorYears: 1m);

    private static IppsStay Stay(
        decimal weight = 1m,
        IppsDischargeKind kind = IppsDischargeKind.Discharged,
        int los = 4,
        decimal geometricMean = 4m,
        decimal charges = 0m,
        int drg = 470,
        IppsPostAcuteTransferRule transferRule = IppsPostAcuteTransferRule.None,
        IppsBeneficiaryDays? days = null) =>
        new(drg, weight, geometricMean, los, charges, kind, transferRule, days);

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

        // Above a wage index of 1.0 CMS uses the annual rule's larger labor
        // share: 0.60 × 1.5 + 0.40 = 1.30. It also happens to be the share
        // that pays more here, which is why the two readings of 412.64(h)(3)
        // agree on this row.
        Assert.Equal(3600m, payment.Operating.StandardizedAmount.LaborRelated);
        Assert.Equal(StandardizedAmount * 1.30m, payment.Operating.WageAdjustedStandardizedAmount);
    }

    [Fact]
    public void Below_a_wage_index_of_one_the_smaller_labor_share_pays_more_and_is_the_one_used()
    {
        var payment = IppsPaymentCalculator.Pay(
            Year(), IppsProvider.Plain("123456", wageIndex: 0.5m), Stay());

        // At or below 1.0 CMS uses the statutory share: 0.50 × 0.5 + 0.50 =
        // 0.75, against 0.60 × 0.5 + 0.40 = 0.70. Again both readings agree,
        // because the cost-of-living adjustment is 1.0.
        Assert.Equal(3000m, payment.Operating.StandardizedAmount.LaborRelated);
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

        // And the share is chosen by the wage index alone, which is where the
        // regulation and CMS's program part company. At a wage index of 1.0
        // the statutory split applies, giving 0.50 × 1.0 + 0.50 × 1.25 =
        // 1.125. Read as 412.64(h)(3) is written -- whichever share pays more
        // -- that is also the answer here, because 0.60 × 1.0 + 0.40 × 1.25 =
        // 1.10 is less. The two readings come apart above a wage index of 1.0,
        // which is the next test.
        Assert.Equal(StandardizedAmount * 1.125m, payment.Operating.WageAdjustedStandardizedAmount);
    }

    [Fact]
    public void A_cost_of_living_adjustment_does_not_make_the_calculator_pick_the_other_share()
    {
        // This is the row the parity run reversed. At a wage index above 1.0
        // with a cost-of-living adjustment above 1.0 -- an Alaskan or Hawaiian
        // hospital -- the two readings of 412.64(h)(3) give different answers,
        // and CMS's pricer follows the wage index.
        //
        // Larger share:  0.60 × 1.2 + 0.40 × 1.25 = 1.22
        // Statutory:     0.50 × 1.2 + 0.50 × 1.25 = 1.225   <- pays more
        //
        // So the regulation's "unless employing that percentage would result
        // in lower payments" reading would take the statutory share and pay
        // 1.225. CMS takes the larger share and pays 1.22. The amount CMS pays
        // is the amount that is correct, so this calculator pays 1.22 and an
        // earlier version of it overpaid every hospital in those two states.
        var provider = IppsProvider.Plain("123456", wageIndex: 1.2m) with
        {
            CostOfLivingAdjustment = 1.25m
        };

        var payment = IppsPaymentCalculator.Pay(Year(), provider, Stay());

        Assert.Equal(3600m, payment.Operating.StandardizedAmount.LaborRelated);
        Assert.Equal(StandardizedAmount * 1.22m, payment.Operating.WageAdjustedStandardizedAmount);
        Assert.NotEqual(
            StandardizedAmount * 1.225m, payment.Operating.WageAdjustedStandardizedAmount);
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
    //  The operating rate table (CMS's 2050-RATES-TB)
    // ─────────────────────────────────────────────────────────

    [Fact]
    public void A_hospital_that_did_not_submit_quality_data_is_paid_a_smaller_standardized_amount()
    {
        var withheld = IppsProvider.Plain("123456", wageIndex: 1.5m) with
        {
            QualityDataSubmitted = false
        };

        var payment = IppsPaymentCalculator.Pay(Year(), withheld, Stay());

        // The reduction is a smaller standardized amount, not a percentage
        // taken off the payment: 5,000 split 3,000/2,000 rather than 6,000
        // split 3,600/2,400. So it scales with the wage index and the DRG
        // weight exactly as the full amount does, and 3,000 × 1.5 + 2,000 is
        // 6,500 where the full row pays 7,800.
        Assert.Equal(3000m, payment.Operating.StandardizedAmount.LaborRelated);
        Assert.Equal(6500m, payment.Operating.WageAdjustedStandardizedAmount);
    }

    [Fact]
    public void The_two_payment_reductions_are_independent_and_a_hospital_can_carry_both()
    {
        var provider = IppsProvider.Plain("123456", wageIndex: 1.5m);
        var full = IppsPaymentCalculator.Pay(Year(), provider, Stay());
        var records = IppsPaymentCalculator.Pay(
            Year(), provider with { ElectronicRecordsReduction = true }, Stay());
        var both = IppsPaymentCalculator.Pay(
            Year(),
            provider with { QualityDataSubmitted = false, ElectronicRecordsReduction = true },
            Stay());

        // Four standardized amounts, one per combination, which is why CMS
        // publishes eight rows and not a reduction factor: 6,000, 4,000 and
        // 3,000 here. A hospital carrying both is paid the fourth row rather
        // than two reductions applied in turn.
        Assert.Equal(6000m, full.Operating.StandardizedAmount.Total);
        Assert.Equal(4000m, records.Operating.StandardizedAmount.Total);
        Assert.Equal(3000m, both.Operating.StandardizedAmount.Total);
    }

    [Fact]
    public void The_wage_index_chooses_the_row_within_a_pair_and_not_the_pair()
    {
        var provider = IppsProvider.Plain("123456", wageIndex: 0.8m) with
        {
            ElectronicRecordsReduction = true
        };

        var payment = IppsPaymentCalculator.Pay(Year(), provider, Stay());

        // At or below a wage index of 1.0 the record-reduced hospital is paid
        // the 50/50 split of its own 4,000, not the 50/50 split of the full
        // 6,000: the two axes are independent, and crossing them is the kind
        // of mistake a single reduction factor invites.
        Assert.Equal(4000m, payment.Operating.StandardizedAmount.Total);
        Assert.Equal(2000m, payment.Operating.StandardizedAmount.LaborRelated);
        Assert.Equal(3600m, payment.Operating.WageAdjustedStandardizedAmount);
    }

    [Theory]
    [InlineData(true, false, true, 1)]
    [InlineData(false, false, true, 2)]
    [InlineData(true, false, false, 3)]
    [InlineData(false, false, false, 4)]
    [InlineData(true, true, true, 5)]
    [InlineData(false, true, true, 6)]
    [InlineData(true, true, false, 7)]
    [InlineData(false, true, false, 8)]
    public void The_eight_rows_are_numbered_as_CMS_numbers_them(
        bool qualityData, bool recordsReduction, bool wageIndexAboveOne, int cmsTableNumber)
    {
        // Pinned against CMS's own branch, which is 2050-RATES-TB in the COBOL
        // and DetermineRateTableWith(out)Reduction in its Java. The mapping is
        // the one thing here that cannot be derived from the regulation, and
        // getting it wrong pays a reduced hospital in full or a full hospital
        // reduced — a plausible amount either way.
        var key = new IppsRateTableKey(qualityData, recordsReduction, wageIndexAboveOne);

        Assert.Equal(cmsTableNumber, key.CmsTableNumber);
    }

    [Fact]
    public void A_half_loaded_rate_table_is_refused_rather_than_paid_from()
    {
        var rows = new Dictionary<int, IppsStandardizedAmount>
        {
            [1] = new(3600m, 2400m),
            [3] = new(3000m, 3000m)
        };

        // A fiscal year loaded with two of its eight rows would pay most
        // hospitals correctly and the reduced ones from whatever row the
        // lookup happened to find. Failing names the missing rows instead.
        var thrown = Assert.Throws<ArgumentException>(() => new IppsOperatingRateTable(rows));

        Assert.Contains("tab2", thrown.Message);
        Assert.Contains("tab8", thrown.Message);
    }

    // ─────────────────────────────────────────────────────────
    //  The hospital-specific portion (42 CFR 412.73, 412.77, 412.108)
    // ─────────────────────────────────────────────────────────

    /// <summary>
    /// A hospital in one of the three categories, with its own base rate on
    /// file. At a wage index of 1.0 on a weight of 1.0 the federal operating
    /// side pays 6,000 and the capital side 500, so a facility rate of 8,000
    /// wins the comparison by 2,000 and a rate of 4,000 loses it.
    /// </summary>
    private static IppsProvider Category(
        IppsHospitalSpecificCategory category, decimal facilityRate = 8000m) =>
        IppsProvider.Plain("123456", wageIndex: 1m) with
        {
            HospitalSpecificCategory = category,
            FacilitySpecificRate = facilityRate
        };

    [Fact]
    public void A_hospital_in_none_of_the_categories_is_paid_no_portion_whatever_rate_is_on_file()
    {
        // A rate high enough to win several times over, at a hospital CMS does
        // not pay a hospital-specific portion to. CMS reads the provider type
        // and never looks at the rate, and a loader that populated the rate
        // from the provider file without the type must not start paying it.
        var payment = IppsPaymentCalculator.Pay(
            Year(), Category(IppsHospitalSpecificCategory.None, facilityRate: 20_000m), Stay());

        Assert.Equal(0m, payment.HospitalSpecific.Payment);
        Assert.Equal(6_500m, payment.Total);
    }

    [Fact]
    public void A_sole_community_hospital_is_paid_the_difference_where_its_own_rate_wins()
    {
        var payment = IppsPaymentCalculator.Pay(
            Year(), Category(IppsHospitalSpecificCategory.SoleCommunity), Stay());

        // 8,000 against the 6,000 the federal operating side paid: the
        // difference, and not the whole of its own rate.
        Assert.Equal(8_000m, payment.HospitalSpecific.Rate);
        Assert.Equal(6_000m, payment.HospitalSpecific.FederalAmountCompared);
        Assert.Equal(2_000m, payment.HospitalSpecific.Payment);
        Assert.Equal(8_500m, payment.Total);
    }

    [Fact]
    public void A_sole_community_hospital_whose_own_rate_loses_is_paid_nothing_extra()
    {
        var payment = IppsPaymentCalculator.Pay(
            Year(),
            Category(IppsHospitalSpecificCategory.SoleCommunity, facilityRate: 4_000m),
            Stay());

        // The comparison is a comparison. An implementation that added the two
        // together would pay 10,500 here and would agree with the test above.
        Assert.Equal(4_000m, payment.HospitalSpecific.Rate);
        Assert.Equal(0m, payment.HospitalSpecific.Payment);
        Assert.Equal(6_500m, payment.Total);
    }

    [Fact]
    public void A_medicare_dependent_hospital_is_paid_three_quarters_of_the_difference()
    {
        var payment = IppsPaymentCalculator.Pay(
            Year(), Category(IppsHospitalSpecificCategory.MedicareDependent), Stay());

        // 412.108(c)(2)(ii): 0.75 × 2,000. The one thing separating the three
        // categories, and a 25 percent overpayment with no other symptom if a
        // loader puts provider type 14 into the sole community branch.
        Assert.Equal(1_500m, payment.HospitalSpecific.Payment);
        Assert.Equal(8_000m, payment.Total);
    }

    [Fact]
    public void An_essential_access_hospital_is_paid_on_the_sole_community_terms()
    {
        var sole = IppsPaymentCalculator.Pay(
            Year(), Category(IppsHospitalSpecificCategory.SoleCommunity), Stay());
        var essential = IppsPaymentCalculator.Pay(
            Year(), Category(IppsHospitalSpecificCategory.EssentialAccessCommunity), Stay());

        Assert.Equal(sole.HospitalSpecific.Payment, essential.HospitalSpecific.Payment);
        Assert.Equal(2_000m, essential.HospitalSpecific.Payment);
    }

    [Fact]
    public void The_comparison_counts_the_uncompensated_care_payment_and_no_part_of_the_capital_side()
    {
        var provider = Category(IppsHospitalSpecificCategory.SoleCommunity) with
        {
            UncompensatedCarePerDischarge = 1_000m
        };

        var payment = IppsPaymentCalculator.Pay(Year(), provider, Stay());

        // The federal side of the comparison is 6,000 plus the 1,000
        // uncompensated care payment, so the portion falls to 1,000. The 500
        // capital payment is not in it: if it were, the portion would be 500,
        // and the hospital would be paid less than its own rate for the case
        // while CMS pays it more.
        Assert.Equal(7_000m, payment.HospitalSpecific.FederalAmountCompared);
        Assert.Equal(1_000m, payment.HospitalSpecific.Payment);
    }

    [Fact]
    public void The_operating_outlier_is_inside_the_comparison_and_the_capital_outlier_is_not()
    {
        var provider = Category(IppsHospitalSpecificCategory.SoleCommunity, facilityRate: 40_000m)
            with
            {
                OperatingCostToChargeRatio = 0.30m,
                CapitalCostToChargeRatio = 0.05m
            };

        var payment = IppsPaymentCalculator.Pay(Year(), provider, Stay(charges: 150_000m));

        // Both sides paid an outlier, which is what makes the row worth
        // having: the operating one is inside the comparison dollar for dollar
        // and the capital one is outside it entirely.
        Assert.True(payment.Outlier.Operating.Payment > 0m);
        Assert.True(payment.Outlier.Capital.Payment > 0m);
        Assert.Equal(
            6_000m + payment.Outlier.Operating.Payment,
            payment.HospitalSpecific.FederalAmountCompared);
        Assert.Equal(
            40_000m - 6_000m - payment.Outlier.Operating.Payment,
            payment.HospitalSpecific.Payment);
    }

    [Fact]
    public void A_transfer_weights_the_federal_side_down_and_the_hospitals_own_rate_not_at_all()
    {
        var payment = IppsPaymentCalculator.Pay(
            Year(),
            Category(IppsHospitalSpecificCategory.SoleCommunity),
            Stay(kind: IppsDischargeKind.TransferredToAcuteCare, los: 1, geometricMean: 4m));

        // Two per diems out of four, so the federal side is halved to 3,000 —
        // while the hospital's own rate keeps the FULL DRG weight and stays at
        // 8,000. The portion is therefore 5,000 and not the 1,000 a
        // symmetrically weighted comparison would pay. That asymmetry is in
        // CMS's program rather than in the regulation, which is why the corpus
        // carries a sole-community transfer as well as this test.
        Assert.Equal(0.5m, payment.Transfer!.Fraction);
        Assert.Equal(8_000m, payment.HospitalSpecific.Rate);
        Assert.Equal(3_000m, payment.HospitalSpecific.FederalAmountCompared);
        Assert.Equal(5_000m, payment.HospitalSpecific.Payment);
    }

    [Fact]
    public void The_portion_is_inside_the_low_volume_base_and_the_penalty_base()
    {
        var provider = Category(IppsHospitalSpecificCategory.SoleCommunity) with
        {
            LowVolumeTemporaryRelief = true,
            LowVolumeAdjustmentFactor = 0.25m,
            HospitalAcquiredConditionReduction = true
        };

        var payment = IppsPaymentCalculator.Pay(Year(), provider, Stay());

        // Case payment 6,500 plus a 2,000 portion is 8,500. The low-volume
        // uplift is a quarter of all of it, 2,125, and the penalty is one
        // percent of the 10,625 that follows: -106.25. Both reach the portion,
        // which is the whole reason it has to be settled before them.
        Assert.Equal(8_500m, payment.PaymentBeforeAdjustments);
        Assert.Equal(2_125m, payment.Adjustments.LowVolume);
        Assert.Equal(-106.25m, payment.Adjustments.HospitalAcquiredConditionPenalty);
        Assert.Equal(10_518.75m, payment.Total);
    }

    [Fact]
    public void The_portion_is_outside_the_readmissions_and_value_based_base()
    {
        var provider = Category(IppsHospitalSpecificCategory.SoleCommunity) with
        {
            ReadmissionsAdjustmentFactor = 0.97m
        };

        var payment = IppsPaymentCalculator.Pay(Year(), provider, Stay());

        // -0.03 × 6,000. CMS's base for these two is the federal specific
        // portion plus any new-technology add-on, so the 2,000 portion is not
        // in it: a base of 8,000 would make this -240.
        Assert.Equal(2_000m, payment.HospitalSpecific.Payment);
        Assert.Equal(-180m, payment.Adjustments.Readmissions);
    }

    [Fact]
    public void The_update_factor_is_the_three_published_factors_multiplied_and_rounded_to_five_places()
    {
        var update = new IppsHospitalSpecificUpdate(
            WithQualityData: 1.0135m,
            WithQualityDataAndRecordsReduction: 1m,
            WithoutQualityData: 1m,
            WithoutQualityDataAndRecordsReduction: 1m,
            BudgetNeutrality: 0.99719m,
            CumulativePriorYears: 1.02m);

        // 1.0135 × 1.02 × 0.99719 = 1.0308651063, and CMS rounds it to five
        // places before it multiplies anything by it. The rounding is part of
        // the amount, not presentation: on a facility rate of 10,000 the
        // unrounded factor pays 10,308.651063 and CMS pays 10,308.70.
        Assert.Equal(
            1.03087m,
            update.FactorFor(IppsProvider.Plain("123456", wageIndex: 1m)));
    }

    [Theory]
    [InlineData(true, false, 1.0)]
    [InlineData(true, true, 0.9)]
    [InlineData(false, false, 0.95)]
    [InlineData(false, true, 0.85)]
    public void The_two_payment_reduction_flags_choose_the_update_factor_and_the_wage_index_does_not(
        bool qualityData, bool recordsReduction, double expected)
    {
        var update = HospitalSpecificUpdate();

        foreach (var wageIndex in new[] { 0.8m, 1m, 1.4m })
        {
            var provider = IppsProvider.Plain("123456", wageIndex) with
            {
                QualityDataSubmitted = qualityData,
                ElectronicRecordsReduction = recordsReduction
            };

            // The hospital-specific portion is the hospital's own rate, so
            // nothing geographic touches it — unlike the eight-row federal
            // table, where the same two flags are crossed with the wage index.
            Assert.Equal((decimal)expected, update.FactorFor(provider));
        }
    }

    // ─────────────────────────────────────────────────────────
    //  The four hospital-level adjustments, and their order
    // ─────────────────────────────────────────────────────────

    /// <summary>
    /// A hospital with an outlier and an uncompensated care payment, so that
    /// an adjustment taken on the wrong base is visible rather than hidden
    /// inside a DRG amount.
    /// </summary>
    private static IppsProvider WithOutlierAndUncompensatedCare() =>
        IppsProvider.Plain("123456", wageIndex: 1m) with
        {
            OperatingCostToChargeRatio = 0.30m,
            UncompensatedCarePerDischarge = 1000m
        };

    [Fact]
    public void A_hospital_in_none_of_the_programmes_gets_no_adjustments()
    {
        var payment = IppsPaymentCalculator.Pay(
            Year(), IppsProvider.Plain("123456", 1m), Stay());

        Assert.Equal(0m, payment.Adjustments.Total);
        Assert.Equal(payment.PaymentBeforeAdjustments, payment.Total);
    }

    [Fact]
    public void The_readmissions_adjustment_is_the_factor_less_one_on_the_base_drg_payment()
    {
        var provider = WithOutlierAndUncompensatedCare() with
        {
            ReadmissionsAdjustmentFactor = 0.98m
        };

        var payment = IppsPaymentCalculator.Pay(Year(), provider, Stay());

        // On the base DRG payment alone: -0.02 × 6,000 = -120. Not on the
        // uncompensated care payment, which would make it -140, and not on the
        // whole operating side.
        Assert.Equal(-120m, payment.Adjustments.Readmissions);
    }

    [Fact]
    public void The_value_based_purchasing_adjustment_can_pay_more_where_readmissions_cannot()
    {
        var provider = IppsProvider.Plain("123456", 1m) with
        {
            ValueBasedPurchasingAdjustmentFactor = 1.01m
        };

        var payment = IppsPaymentCalculator.Pay(Year(), provider, Stay());

        // CMS's range for this one is 0.98 to 2.0, so above 1.0 is a payment
        // and not an error. The readmissions range stops at 1.0, which is why
        // the two are separate inputs rather than one adjustment factor.
        Assert.Equal(60m, payment.Adjustments.ValueBasedPurchasing);
    }

    [Theory]
    [InlineData(0.96)]
    [InlineData(1.01)]
    public void A_readmissions_factor_outside_CMS_range_is_refused_rather_than_paid(decimal factor)
    {
        var provider = IppsProvider.Plain("123456", 1m) with
        {
            ReadmissionsAdjustmentFactor = factor
        };

        // CMS returns return code 65 and does not price the claim. Paying it
        // anyway would pay an amount nobody published, from a provider file
        // that is wrong.
        Assert.Throws<IppsPaymentFailedException>(
            () => IppsPaymentCalculator.Pay(Year(), provider, Stay()));
    }

    [Fact]
    public void The_low_volume_uplift_is_taken_on_the_outlier_and_uncompensated_care_too()
    {
        var provider = WithOutlierAndUncompensatedCare() with
        {
            LowVolumeTemporaryRelief = true,
            LowVolumeAdjustmentFactor = 0.10m
        };

        var payment = IppsPaymentCalculator.Pay(Year(), provider, Stay(charges: 400_000m));

        // 412.101's uplift is a percentage of almost the whole payment, not of
        // the DRG amount: both sides, the cost outlier and the uncompensated
        // care payment. A calculator taking it on the base payment pays far
        // less on exactly the outlier cases where it matters most.
        Assert.True(payment.Outlier.Operating.Payment > 0m);
        Assert.Equal(0.10m * payment.PaymentBeforeAdjustments, payment.Adjustments.LowVolume);
    }

    [Fact]
    public void The_low_volume_uplift_is_not_taken_on_the_other_two_adjustments()
    {
        var provider = IppsProvider.Plain("123456", 1m) with
        {
            LowVolumeTemporaryRelief = true,
            LowVolumeAdjustmentFactor = 0.20m,
            ReadmissionsAdjustmentFactor = 0.98m
        };

        var payment = IppsPaymentCalculator.Pay(Year(), provider, Stay());

        // CMS computes the uplift on lines one to eight of its sum and takes
        // the readmissions and value-based adjustments afterwards, so the
        // uplift is 0.20 × 6,500 and not 0.20 × (6,500 - 120).
        Assert.Equal(0.20m * payment.PaymentBeforeAdjustments, payment.Adjustments.LowVolume);
        Assert.Equal(-120m, payment.Adjustments.Readmissions);
    }

    [Fact]
    public void A_low_volume_factor_above_the_published_maximum_pays_nothing_and_is_not_capped()
    {
        var provider = IppsProvider.Plain("123456", 1m) with
        {
            LowVolumeTemporaryRelief = true,
            LowVolumeAdjustmentFactor = 0.30m
        };

        var payment = IppsPaymentCalculator.Pay(Year(), provider, Stay());

        // CMS's gate is `> 0 and <= 0.25`, and a factor above it falls through
        // to zero. Capping at 0.25 would pay a hospital whose provider file is
        // wrong, and pay it the maximum.
        Assert.Equal(0m, payment.Adjustments.LowVolume);
        Assert.Contains(payment.Notes, note => note.Contains("rather than capping"));
    }

    [Fact]
    public void The_low_volume_adjustment_needs_the_approval_as_well_as_the_factor()
    {
        var factorOnly = IppsProvider.Plain("123456", 1m) with
        {
            LowVolumeAdjustmentFactor = 0.20m
        };

        var payment = IppsPaymentCalculator.Pay(Year(), factorOnly, Stay());

        // Two gates. A factor on file at a hospital whose low-volume status was
        // not approved for the year pays nothing.
        Assert.Equal(0m, payment.Adjustments.LowVolume);
    }

    [Fact]
    public void The_hospital_acquired_condition_penalty_reaches_the_outlier()
    {
        var provider = WithOutlierAndUncompensatedCare() with
        {
            HospitalAcquiredConditionReduction = true
        };

        var payment = IppsPaymentCalculator.Pay(Year(), provider, Stay(charges: 400_000m));

        // One percent of everything above it. The penalty is written as a
        // percentage of the payment rather than of the DRG amount, so on an
        // outlier case it is far larger than one percent of the DRG — which is
        // the arithmetic a "one percent reduction" reading of 412.170 misses.
        Assert.True(payment.Outlier.Payment > 0m);
        Assert.Equal(
            -0.01m * payment.PaymentBeforeAdjustments,
            payment.Adjustments.HospitalAcquiredConditionPenalty);
    }

    [Fact]
    public void The_penalty_is_taken_after_the_other_three_and_so_on_their_total()
    {
        var provider = IppsProvider.Plain("123456", 1m) with
        {
            LowVolumeTemporaryRelief = true,
            LowVolumeAdjustmentFactor = 0.25m,
            ReadmissionsAdjustmentFactor = 0.97m,
            ValueBasedPurchasingAdjustmentFactor = 1.02m,
            HospitalAcquiredConditionReduction = true
        };

        var payment = IppsPaymentCalculator.Pay(Year(), provider, Stay());

        // The order, end to end. Case payment 6,500 (6,000 operating + 500
        // capital); low volume 0.25 × 6,500 = 1,625; readmissions -0.03 ×
        // 6,000 = -180; value-based 0.02 × 6,000 = 120. The penalty is one
        // percent of all of that: -0.01 × 8,065 = -80.65.
        Assert.Equal(6500m, payment.PaymentBeforeAdjustments);
        Assert.Equal(1625m, payment.Adjustments.LowVolume);
        Assert.Equal(-180m, payment.Adjustments.Readmissions);
        Assert.Equal(120m, payment.Adjustments.ValueBasedPurchasing);
        Assert.Equal(-80.65m, payment.Adjustments.HospitalAcquiredConditionPenalty);
        Assert.Equal(7984.35m, payment.Total);
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
        Assert.Equal(2m / 10m, payment.Transfer.Fraction);
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
                transferRule: IppsPostAcuteTransferRule.HalfAndHalf));

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
                transferRule: IppsPostAcuteTransferRule.HalfAndHalf));

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
    public void An_outlier_pays_eighty_percent_of_the_cost_above_each_threshold()
    {
        var provider = WithRatios(0.30m, 0.05m);
        var payment = IppsPaymentCalculator.Pay(Year(), provider, Stay(charges: 400_000m));

        // 412.84(k). The operating cost is 400,000 x 0.30 and the capital cost
        // 400,000 x 0.05, each tested against its own side's payment plus its
        // own share of the fixed loss amount.
        Assert.Equal(120_000m, payment.Outlier.Operating.EstimatedCost);
        Assert.Equal(20_000m, payment.Outlier.Capital.EstimatedCost);
        Assert.Equal(140_000m, payment.Outlier.EstimatedCost);
        Assert.Equal(0.80m, payment.Outlier.MarginalCostFactor);

        var expected =
            (0.80m * (120_000m - payment.Outlier.Operating.Threshold))
            + (0.80m * (20_000m - payment.Outlier.Capital.Threshold));
        Assert.Equal(expected, payment.Outlier.Payment);
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

        // 412.84(g) keeps them separate, and each side's cost is its own.
        Assert.Equal(30_000m, operatingOnly.Outlier.EstimatedCost);
        Assert.Equal(0m, operatingOnly.Outlier.Capital.EstimatedCost);
        Assert.Equal(35_000m, both.Outlier.EstimatedCost);
    }

    [Fact]
    public void A_hospital_with_only_an_operating_ratio_bears_the_whole_fixed_loss_amount()
    {
        // The apportionment is by cost-to-charge ratio, so a hospital with no
        // capital ratio puts all of the fixed loss amount on the operating
        // side rather than losing the capital share of it.
        var payment = IppsPaymentCalculator.Pay(
            Year(), WithRatios(0.30m, 0m), Stay(charges: 400_000m));

        Assert.Equal(30_000m, payment.Outlier.Operating.FixedLossShare);
        Assert.Equal(0m, payment.Outlier.Capital.FixedLossShare);
    }

    [Fact]
    public void The_operating_fixed_loss_share_is_wage_adjusted_like_the_standardized_amount()
    {
        var provider = WithRatios(0.30m, 0m) with { WageIndex = 1.5m };

        var payment = IppsPaymentCalculator.Pay(Year(), provider, Stay(charges: 400_000m));

        // The same 1.30 multiplier the standardized amount got. With no capital
        // ratio the operating side carries the whole amount, which keeps the
        // apportionment out of the arithmetic being checked here.
        Assert.Equal(30_000m * 1.30m, payment.Outlier.Operating.FixedLossShare);
        Assert.Equal(
            payment.Outlier.Operating.Threshold - (30_000m * 1.30m),
            payment.Operating.Total);
    }

    [Fact]
    public void The_capital_fixed_loss_share_is_adjusted_by_the_capital_geographic_factor()
    {
        // Capital has no labor split: 412.84 adjusts the whole capital rate by
        // one geographic factor, and the capital side of the threshold with it.
        // A calculator that reused the labor split here would get a different
        // number for every hospital whose wage index and geographic adjustment
        // factor differ, which is most of them.
        var provider = WithRatios(0m, 0.05m) with
        {
            WageIndex = 1.5m,
            CapitalGeographicAdjustmentFactor = 1.2m
        };

        var payment = IppsPaymentCalculator.Pay(Year(), provider, Stay(charges: 400_000m));

        Assert.Equal(30_000m * 1.2m, payment.Outlier.Capital.FixedLossShare);
    }

    [Fact]
    public void The_fixed_loss_amount_is_apportioned_once_and_not_applied_twice()
    {
        var provider = WithRatios(0.30m, 0.05m);
        var payment = IppsPaymentCalculator.Pay(Year(), provider, Stay(charges: 400_000m));

        // The history here is worth the test. The first draft tested each side
        // against the payment plus the *whole* fixed loss amount, which counts
        // it twice. The correction added the two costs and tested them against
        // one threshold, which is not what CMS does either. CMS apportions the
        // one amount between the sides by cost-to-charge ratio, so the two
        // shares add back to it -- here undivided by any geographic factor,
        // because this hospital's are all 1.0.
        var apportioned =
            payment.Outlier.Operating.FixedLossShare + payment.Outlier.Capital.FixedLossShare;
        Assert.Equal(30_000m, Math.Round(apportioned, 6));

        // And the shares are in the ratio of the two cost-to-charge ratios.
        Assert.Equal(
            Math.Round(0.30m / 0.05m, 6),
            Math.Round(
                payment.Outlier.Operating.FixedLossShare
                    / payment.Outlier.Capital.FixedLossShare,
                6));
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

        // 412.4(f)(4) allows an outlier on a transfer, and each side's
        // threshold is measured from what that side is paid.
        Assert.True(
            transferred.Outlier.Operating.Threshold < discharged.Outlier.Operating.Threshold);
        Assert.True(transferred.Outlier.Payment > discharged.Outlier.Payment);
    }

    // ─────────────────────────────────────────────────────────
    //  Pass-through costs, and where they sit in the total
    // ─────────────────────────────────────────────────────────

    [Fact]
    public void Pass_through_costs_are_paid_per_covered_day_and_not_per_discharge()
    {
        var provider = IppsProvider.Plain("123456", wageIndex: 1m) with
        {
            PassThroughPerDiem = 100m
        };

        var short_ = IppsPaymentCalculator.Pay(Year(), provider, Stay(los: 3));
        var long_ = IppsPaymentCalculator.Pay(Year(), provider, Stay(los: 30));

        // CMS multiplies the provider file's figure by the length of stay, so a
        // ten-times-longer stay is paid ten times the pass-through. The field
        // CMS holds it in is named as though it were a total, which is the
        // mistake this test exists to catch.
        Assert.Equal(300m, short_.PassThroughPayment);
        Assert.Equal(3_000m, long_.PassThroughPayment);
    }

    [Fact]
    public void Pass_through_costs_escape_the_low_volume_base_and_the_penalty()
    {
        var provider = IppsProvider.Plain("123456", wageIndex: 1m) with
        {
            PassThroughPerDiem = 100m,
            LowVolumeTemporaryRelief = true,
            LowVolumeAdjustmentFactor = 0.25m,
            HospitalAcquiredConditionReduction = true
        };

        var payment = IppsPaymentCalculator.Pay(Year(), provider, Stay(los: 3));

        // CMS adds the pass-through after the penalty, so neither the uplift nor
        // the penalty sees it: the 6,500 case payment gives a 1,625 uplift and a
        // -81.25 penalty, exactly as it would with no pass-through at all, and
        // the 300 lands on top.
        Assert.Equal(6_500m, payment.PaymentBeforeAdjustments);
        Assert.Equal(1_625m, payment.Adjustments.LowVolume);
        Assert.Equal(-81.25m, payment.Adjustments.HospitalAcquiredConditionPenalty);
        Assert.Equal(300m, payment.PassThroughPayment);
        Assert.Equal(8_343.75m, payment.Total);
    }

    [Fact]
    public void A_transfer_does_not_reduce_the_pass_through()
    {
        var provider = IppsProvider.Plain("123456", wageIndex: 1m) with
        {
            PassThroughPerDiem = 100m
        };

        var payment = IppsPaymentCalculator.Pay(
            Year(),
            provider,
            Stay(kind: IppsDischargeKind.TransferredToAcuteCare, los: 1, geometricMean: 4m));

        // The figure is already a per diem, so the days of the stay are the only
        // thing that scales it. Applying the transfer fraction on top would pay
        // half a day's costs for a day the patient was there. Note this is the
        // claim's length of stay, one day, and not the two per diems 412.4(f)(1)
        // pays the DRG by.
        Assert.Equal(0.5m, payment.Transfer!.Fraction);
        Assert.Equal(100m, payment.PassThroughPayment);
    }

    [Fact]
    public void A_hospital_with_no_pass_through_amount_on_file_is_paid_none()
    {
        var payment = IppsPaymentCalculator.Pay(
            Year(), IppsProvider.Plain("123456", 1m), Stay(los: 30));

        Assert.Equal(0m, payment.PassThroughPayment);
        Assert.Equal(6_500m, payment.Total);
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

    // ─────────────────────────────────────────────────────────
    //  Beneficiary days (42 CFR 409.61, 412.84)
    // ─────────────────────────────────────────────────────────

    [Fact]
    public void Covered_days_shorter_than_the_stay_do_not_reduce_the_payment()
    {
        // The whole point of a prospective payment is that it buys the
        // discharge and not the days, so three covered days out of ten pays
        // what ten out of ten pays. This is the test that would catch a
        // calculator that prorated by the covered fraction, which is the
        // obvious wrong thing to do and the one CMS does not do.
        var fully = IppsPaymentCalculator.Pay(
            Year(),
            IppsProvider.Plain("123456", wageIndex: 1m),
            Stay(los: 10, geometricMean: 10m, days: new IppsBeneficiaryDays(10, 0)));

        var partly = IppsPaymentCalculator.Pay(
            Year(),
            IppsProvider.Plain("123456", wageIndex: 1m),
            Stay(los: 10, geometricMean: 10m, days: new IppsBeneficiaryDays(3, 0)));

        Assert.Equal(fully.Total, partly.Total);
        Assert.Equal(IppsUnpayableReason.None, partly.Unpayable);
    }

    [Fact]
    public void A_stay_with_no_day_counts_is_paid_and_says_the_refusal_was_not_applied()
    {
        var payment = IppsPaymentCalculator.Pay(
            Year(), WithRatios(0.30m, 0.05m), Stay(charges: 400_000m, days: null));

        Assert.True(payment.Outlier.Payment > 0m);
        Assert.Equal(IppsUnpayableReason.None, payment.Unpayable);
        Assert.Equal(IppsDayUtilization.None, payment.Utilization);
        Assert.Contains(
            payment.Notes,
            note => note.Contains("No covered-day counts", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(10, 0, 4, 0)]      // Ten covered days, four days here: four used.
    [InlineData(4, 0, 4, 0)]       // Exactly covered.
    [InlineData(3, 0, 3, 0)]       // Short of the stay: still only the covered ones.
    [InlineData(10, 6, 4, 0)]      // Four regular days left, so four regular used.
    [InlineData(10, 8, 2, 0)]      // Two regular days left: reserve days not reported.
    [InlineData(4, 4, 0, 4)]       // No regular day at all, so the reserve is reported.
    [InlineData(10, 10, 0, 4)]     // Reserve capped at the length of the stay.
    public void Utilization_reports_regular_days_or_reserve_days_but_never_both(
        int covered, int reserve, int expectedRegularUsed, int expectedReserveUsed)
    {
        var payment = IppsPaymentCalculator.Pay(
            Year(),
            IppsProvider.Plain("123456", wageIndex: 1m),
            Stay(los: 4, days: new IppsBeneficiaryDays(covered, reserve)));

        Assert.Equal(expectedRegularUsed, payment.Utilization.RegularDaysUsed);
        Assert.Equal(expectedReserveUsed, payment.Utilization.LifetimeReserveDaysUsed);
    }

    [Fact]
    public void Nine_reserve_days_under_three_regular_ones_are_reported_as_none_used()
    {
        // CMS's own quirk, kept deliberately: where any regular day remains it
        // reports the regular days and reports zero reserve days, though nine
        // reserve days were plainly consumed. A caller that needs to decrement
        // the beneficiary's reserve balance cannot read it off this.
        var payment = IppsPaymentCalculator.Pay(
            Year(),
            IppsProvider.Plain("123456", wageIndex: 1m),
            Stay(los: 12, geometricMean: 12m, days: new IppsBeneficiaryDays(12, 9)));

        Assert.Equal(3, payment.Utilization.RegularDaysUsed);
        Assert.Equal(0, payment.Utilization.LifetimeReserveDaysUsed);
    }

    [Fact]
    public void A_cost_outlier_that_outran_the_covered_days_is_paid_nothing()
    {
        var provider = WithRatios(0.30m, 0.05m);

        var payment = IppsPaymentCalculator.Pay(
            Year(),
            provider,
            Stay(los: 12, geometricMean: 12m, charges: 400_000m,
                days: new IppsBeneficiaryDays(3, 0)));

        Assert.Equal(IppsUnpayableReason.CostOutlierBeyondCoveredDays, payment.Unpayable);
        Assert.Equal(0m, payment.Total);

        // The amounts CMS computed before discarding them survive, because the
        // reason the claim was refused is usually in one of them.
        Assert.True(payment.Outlier.Payment > 0m);
        Assert.True(payment.Outlier.Operating.Threshold > 0m);
        Assert.Equal(IppsDayUtilization.None, payment.Utilization);
    }

    [Fact]
    public void One_uncovered_day_is_enough_to_refuse_an_outlier_stay()
    {
        var provider = WithRatios(0.30m, 0.05m);
        var stay = Stay(los: 12, geometricMean: 12m, charges: 400_000m,
            days: new IppsBeneficiaryDays(12, 0));

        var covered = IppsPaymentCalculator.Pay(Year(), provider, stay);
        var oneShort = IppsPaymentCalculator.Pay(
            Year(), provider, stay with { Days = new IppsBeneficiaryDays(11, 0) });

        Assert.True(covered.Total > 0m);
        Assert.Equal(0m, oneShort.Total);
    }

    [Fact]
    public void Reserve_days_count_towards_the_covered_days_the_outlier_test_uses()
    {
        // CMS writes the test as regular days plus reserve days, and that sum
        // is the covered days by construction, so splitting the covered days
        // into reserve days cannot change the answer.
        var provider = WithRatios(0.30m, 0.05m);

        var plain = IppsPaymentCalculator.Pay(
            Year(),
            provider,
            Stay(los: 12, geometricMean: 12m, charges: 400_000m,
                days: new IppsBeneficiaryDays(12, 0)));

        var reserve = IppsPaymentCalculator.Pay(
            Year(),
            provider,
            Stay(los: 12, geometricMean: 12m, charges: 400_000m,
                days: new IppsBeneficiaryDays(12, 9)));

        Assert.Equal(IppsUnpayableReason.None, reserve.Unpayable);
        Assert.Equal(plain.Total, reserve.Total);
    }

    [Fact]
    public void A_partly_covered_stay_with_no_outlier_is_paid_in_full()
    {
        // The refusal is about the outlier, not about the covered days: with no
        // cost-to-charge ratio on file there is no outlier and nothing to
        // refuse, on exactly the stay that would otherwise be refused.
        var payment = IppsPaymentCalculator.Pay(
            Year(),
            IppsProvider.Plain("123456", wageIndex: 1m),
            Stay(los: 12, geometricMean: 12m, charges: 400_000m,
                days: new IppsBeneficiaryDays(3, 0)));

        Assert.Equal(0m, payment.Outlier.Payment);
        Assert.Equal(IppsUnpayableReason.None, payment.Unpayable);
        Assert.True(payment.Total > 0m);
    }

    [Fact]
    public void Sixty_one_reserve_days_is_more_than_a_beneficiary_has_in_a_lifetime()
    {
        var failure = Assert.Throws<IppsPaymentFailedException>(() =>
            IppsPaymentCalculator.Pay(
                Year(),
                IppsProvider.Plain("123456", wageIndex: 1m),
                Stay(los: 3, days: new IppsBeneficiaryDays(70, 61))));

        Assert.Contains("lifetime", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Sixty_reserve_days_against_three_covered_ones_is_refused_for_the_covered_days()
    {
        // The order of CMS's two edits is visible here: sixty passes the
        // lifetime limit, so what refuses this stay is the covered-day
        // comparison, and the message has to say so or it sends whoever reads
        // it looking at the wrong field.
        var failure = Assert.Throws<IppsPaymentFailedException>(() =>
            IppsPaymentCalculator.Pay(
                Year(),
                IppsProvider.Plain("123456", wageIndex: 1m),
                Stay(los: 3, days: new IppsBeneficiaryDays(3, 60))));

        Assert.Contains("subset", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void No_covered_days_on_a_stay_with_days_in_it_is_refused()
    {
        Assert.Throws<IppsPaymentFailedException>(() =>
            IppsPaymentCalculator.Pay(
                Year(),
                IppsProvider.Plain("123456", wageIndex: 1m),
                Stay(los: 3, days: new IppsBeneficiaryDays(0, 0))));
    }

    [Fact]
    public void No_covered_days_on_a_stay_with_no_days_in_it_is_paid()
    {
        // A same-day discharge has nothing to cover, and CMS pays it.
        var payment = IppsPaymentCalculator.Pay(
            Year(),
            IppsProvider.Plain("123456", wageIndex: 1m),
            Stay(los: 0, days: new IppsBeneficiaryDays(0, 0)));

        Assert.True(payment.Total > 0m);
        Assert.Equal(IppsDayUtilization.None, payment.Utilization);
    }

    private static IppsProvider WithRatios(decimal operating, decimal capital) =>
        IppsProvider.Plain("123456", wageIndex: 1m) with
        {
            OperatingCostToChargeRatio = operating,
            CapitalCostToChargeRatio = capital
        };
}
