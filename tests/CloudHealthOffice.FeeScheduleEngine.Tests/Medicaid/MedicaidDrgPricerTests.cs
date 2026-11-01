using CloudHealthOffice.FeeScheduleEngine.Medicaid;

namespace CloudHealthOffice.FeeScheduleEngine.Tests.Medicaid;

/// <summary>
/// Texas (1 TAC §355.8052) APR-DRG pricing, worked by hand from the rule.
/// </summary>
public class TexasMedicaidDrgPricerTests
{
    private static readonly TexasDrgPolicy Policy = new() { UniversalMean = 5000m };
    private readonly TexasMedicaidDrgPricer _pricer = new(Policy);

    private static MedicaidDrgHospital Hospital(
        TexasHospitalClass cls = TexasHospitalClass.Urban, decimal sda = 6000m, decimal? interim = 0.5m) => new()
    {
        Npi = "1234567893", BaseRate = sda, CostToChargeRatio = interim, TexasClass = cls,
    };

    private static MedicaidDrgWeight Weight(decimal rw, decimal? mlos = null, decimal? dayThreshold = null) => new()
    {
        DrgSoi = "139-2", RelativeWeight = rw, MeanLengthOfStay = mlos, DayOutlierThreshold = dayThreshold,
    };

    private static MedicaidDrgStay Stay(int los = 4, decimal charges = 20000m, int? age = 45, string status = "01") => new()
    {
        DrgSoi = "139-2", LengthOfStayDays = los, AllowedCharges = charges, AgeYearsAtAdmission = age, DischargeStatus = status,
    };

    [Fact]
    public void An_adult_stay_pays_sda_times_weight_and_no_outlier()
    {
        var price = _pricer.Price(Stay(), Weight(1.2m), Hospital());

        Assert.True(price.Priced);
        Assert.Equal(7200m, price.AllowedAmount);
        Assert.Equal(MedicaidOutlierKind.None, price.OutlierKind);
        Assert.Contains(price.Steps, s => s.Contains("$6,000.00") && s.Contains("1.2"));
    }

    [Fact]
    public void An_adult_stay_needs_no_interim_rate_because_adults_have_no_outlier()
    {
        var price = _pricer.Price(Stay(), Weight(1.2m), Hospital(interim: null));

        Assert.True(price.Priced);
        Assert.Equal(7200m, price.AllowedAmount);
    }

    [Fact]
    public void A_transfer_pays_the_per_diem_for_the_days_stayed()
    {
        var price = _pricer.Price(Stay(los: 2, status: "02"), Weight(1.2m, mlos: 5m), Hospital());

        Assert.True(price.TransferAdjusted);
        Assert.Equal(7200m, price.FullDrgPayment);
        Assert.Equal(2880m, price.AllowedAmount); // 7,200 / 5 × 2
    }

    [Fact]
    public void An_adult_transfer_is_capped_at_30_days_and_a_childs_is_not()
    {
        var adult = _pricer.Price(Stay(los: 40, status: "02"), Weight(1.2m, mlos: 45m), Hospital());
        var child = _pricer.Price(Stay(los: 40, status: "02", age: 10), Weight(1.2m, mlos: 45m), Hospital());

        Assert.Equal(4800m, adult.AllowedAmount); // 7,200 / 45 × 30
        Assert.Equal(6400m, child.AllowedAmount); // 7,200 / 45 × 40, no outlier
    }

    [Fact]
    public void A_childs_cost_outlier_is_60_percent_of_cost_over_threshold_at_90_percent_for_urban()
    {
        // DRG 6,000 × 2.0 = 12,000. Threshold = max(min(5,000, 6,000) × 11.14, 1.5 × 12,000) = 55,700.
        // Cost 300,000 × 0.5 = 150,000. (150,000 − 55,700) × 0.6 × 0.9 = 50,922.
        var price = _pricer.Price(Stay(los: 10, charges: 300000m, age: 8), Weight(2.0m, mlos: 5m, dayThreshold: 12m), Hospital());

        Assert.Equal(MedicaidOutlierKind.CostOutlier, price.OutlierKind);
        Assert.Equal(50922m, price.OutlierPayment);
        Assert.Equal(62922m, price.AllowedAmount);
    }

    [Fact]
    public void A_childrens_hospital_is_paid_the_whole_outlier()
    {
        var price = _pricer.Price(
            Stay(los: 10, charges: 300000m, age: 8), Weight(2.0m, mlos: 5m, dayThreshold: 12m),
            Hospital(TexasHospitalClass.Childrens));

        Assert.Equal(56580m, price.OutlierPayment);
        Assert.Equal(68580m, price.AllowedAmount);
    }

    [Fact]
    public void A_day_outlier_is_paid_when_it_is_the_higher_one()
    {
        // DRG 6,000. Per diem 1,500. 12 days over threshold 8 × 1,500 × 0.6 = 10,800;
        // cost 30,000 − 6,000 = 24,000; lesser 10,800 × 0.9 = 9,720. Cost outlier 0.
        var price = _pricer.Price(Stay(los: 20, charges: 60000m, age: 3), Weight(1.0m, mlos: 4m, dayThreshold: 8m), Hospital());

        Assert.Equal(MedicaidOutlierKind.DayOutlier, price.OutlierKind);
        Assert.Equal(9720m, price.OutlierPayment);
        Assert.Equal(15720m, price.AllowedAmount);
    }

    [Fact]
    public void A_stay_within_two_days_of_mlos_is_not_a_day_outlier()
    {
        var price = _pricer.Price(Stay(los: 6, charges: 60000m, age: 3), Weight(1.0m, mlos: 4m, dayThreshold: 5m), Hospital());

        Assert.Equal(MedicaidOutlierKind.None, price.OutlierKind);
        Assert.Equal(6000m, price.AllowedAmount);
    }

    [Fact]
    public void A_child_cannot_be_priced_without_what_the_outlier_needs()
    {
        var noInterim = _pricer.Price(Stay(age: 5), Weight(1m), Hospital(interim: null));
        var noMean = new TexasMedicaidDrgPricer(new TexasDrgPolicy()).Price(Stay(age: 5), Weight(1m), Hospital());
        var noAge = _pricer.Price(Stay(age: null), Weight(1m), Hospital());

        Assert.False(noInterim.Priced);
        Assert.False(noMean.Priced);
        Assert.False(noAge.Priced);
    }

    [Fact]
    public void Missing_weights_and_rates_are_unpriced_not_zero()
    {
        Assert.False(_pricer.Price(Stay(), null, Hospital()).Priced);
        Assert.False(_pricer.Price(Stay(), Weight(1m), Hospital(sda: 0m)).Priced);
        Assert.False(_pricer.Price(Stay(status: "02"), Weight(1m), Hospital()).Priced); // transfer, no MLOS
    }
}

/// <summary>
/// Medi-Cal DRG payment method, worked by hand from the State Plan and the
/// DHCS design document.
/// </summary>
public class MediCalDrgPricerTests
{
    private static readonly MediCalDrgPolicy Policy = new()
    {
        CostOutlierTiers = [new MediCalOutlierTier(99000m, 0.60m)],
        PolicyAdjustors =
        [
            new MediCalPolicyAdjustor("Neonate", 1.85m, 1, 3, DesignatedNicu: true),
            new MediCalPolicyAdjustor("Neonate", 1.35m, 1, 3, DesignatedNicu: false),
        ],
        AgeAdjustors = [new MediCalAgeAdjustor("Pediatric respiratory", 1.25m)],
    };

    private readonly MediCalDrgPricer _pricer = new(Policy);

    private static MedicaidDrgHospital Hospital(
        decimal basePrice = 10000m, decimal? ccr = 0.1m, decimal? wage = null, decimal? labor = null,
        bool nicu = false, bool transitional = false) => new()
    {
        Npi = "1234567893", BaseRate = basePrice, CostToChargeRatio = ccr, WageIndex = wage, LaborShare = labor,
        IsDesignatedNicu = nicu, BaseRateIsTransitional = transitional,
    };

    private static MedicaidDrgWeight Weight(string key = "194-1", decimal rw = 1m, decimal? alos = null, string? category = null) => new()
    {
        DrgSoi = key, RelativeWeight = rw, MeanLengthOfStay = alos, CareCategory = category,
    };

    private static MedicaidDrgStay Stay(string key = "194-1", int los = 4, decimal charges = 20000m, int? age = 45, string status = "01") => new()
    {
        DrgSoi = key, LengthOfStayDays = los, AllowedCharges = charges, AgeYearsAtAdmission = age, DischargeStatus = status,
    };

    [Fact]
    public void The_design_documents_worked_example_prices()
    {
        // DHCS DRG design document: heart failure 194-1, 0.4968 × $7,200 = $3,577.
        var price = _pricer.Price(Stay(charges: 10000m), Weight(rw: 0.4968m), Hospital(basePrice: 7200m, ccr: 0.3m));

        Assert.True(price.Priced);
        Assert.Equal(3576.96m, price.AllowedAmount);
    }

    [Fact]
    public void The_base_price_is_wage_adjusted_by_labor_share()
    {
        // 9,584 × (0.62 × 1.2 + 0.38) = 10,772.416.
        var price = _pricer.Price(Stay(), Weight(), Hospital(basePrice: 9584m, wage: 1.2m, labor: 0.62m));

        Assert.Equal(10772.42m, price.AllowedAmount);
    }

    [Fact]
    public void A_transitional_base_price_is_not_wage_adjusted()
    {
        var price = _pricer.Price(Stay(), Weight(), Hospital(basePrice: 9584m, wage: 1.2m, labor: 0.62m, transitional: true));

        Assert.Equal(9584m, price.AllowedAmount);
    }

    [Theory]
    [InlineData("1", true, 35460.80)]  // 9,584 × 2.0 × 1.85
    [InlineData("1", false, 25876.80)] // 9,584 × 2.0 × 1.35
    [InlineData("4", true, 19168.00)]  // SOI 4: no neonate adjustor in this table
    public void Neonate_adjustors_depend_on_nicu_designation_and_severity(string soi, bool nicu, double expected)
    {
        var key = $"640-{soi}";
        var price = _pricer.Price(
            Stay(key, charges: 1000000m), Weight(key, rw: 2.0m, category: "Neonate"),
            Hospital(basePrice: 9584m, ccr: 0.01m, nicu: nicu));

        Assert.Equal((decimal)expected, price.AllowedAmount);
    }

    [Fact]
    public void The_age_adjustor_applies_under_21_and_needs_an_age()
    {
        var child = _pricer.Price(Stay(age: 5), Weight(category: "Pediatric respiratory"), Hospital());
        var adult = _pricer.Price(Stay(age: 30), Weight(category: "Pediatric respiratory"), Hospital());
        var unknown = _pricer.Price(Stay(age: null), Weight(category: "Pediatric respiratory"), Hospital());

        Assert.Equal(12500m, child.AllowedAmount);
        Assert.Equal(10000m, adult.AllowedAmount);
        Assert.False(unknown.Priced);
    }

    [Theory]
    [InlineData(1, "02", 4000)]   // 10,000 / 5 × (1 + 1)
    [InlineData(6, "02", 10000)]  // 14,000 capped at the DRG payment
    [InlineData(1, "65", 4000)]   // Medi-Cal adds psychiatric transfers
    [InlineData(1, "03", 10000)]  // post-acute (SNF): no transfer reduction
    public void An_acute_transfer_pays_a_per_diem_plus_one_day_capped(int los, string status, decimal expected)
    {
        var price = _pricer.Price(Stay(los: los, status: status), Weight(alos: 5m), Hospital());

        Assert.Equal(expected, price.AllowedAmount);
    }

    [Fact]
    public void The_cost_outlier_pays_the_marginal_percent_of_loss_over_the_threshold()
    {
        // Cost 300,000 × 0.5 = 150,000; loss 140,000; (140,000 − 99,000) × 0.6 = 24,600.
        var price = _pricer.Price(Stay(charges: 300000m), Weight(), Hospital(ccr: 0.5m));

        Assert.Equal(MedicaidOutlierKind.CostOutlier, price.OutlierKind);
        Assert.Equal(24600m, price.OutlierPayment);
        Assert.Equal(34600m, price.AllowedAmount);
    }

    [Fact]
    public void Outlier_tiers_are_paid_piecewise()
    {
        var pricer = new MediCalDrgPricer(Policy with
        {
            CostOutlierTiers = [new MediCalOutlierTier(40000m, 0.60m), new MediCalOutlierTier(125000m, 0.80m)],
        });

        // (125,000 − 40,000) × 0.6 + (140,000 − 125,000) × 0.8 = 51,000 + 12,000.
        var price = pricer.Price(Stay(charges: 300000m), Weight(), Hospital(ccr: 0.5m));

        Assert.Equal(63000m, price.OutlierPayment);
    }

    [Fact]
    public void A_loss_needing_an_outlier_cannot_be_priced_without_configured_tiers()
    {
        var pricer = new MediCalDrgPricer(new MediCalDrgPolicy());

        Assert.False(pricer.Price(Stay(charges: 300000m), Weight(), Hospital(ccr: 0.5m)).Priced);
        Assert.True(pricer.Price(Stay(charges: 20000m), Weight(), Hospital(ccr: 0.1m)).Priced);
    }

    [Fact]
    public void Payment_never_exceeds_charges()
    {
        var price = _pricer.Price(Stay(charges: 8000m), Weight(), Hospital());

        Assert.True(price.ChargeCapApplied);
        Assert.Equal(8000m, price.AllowedAmount);
    }

    [Fact]
    public void A_configured_low_side_outlier_reduces_an_overpaid_stay()
    {
        var pricer = new MediCalDrgPricer(Policy with { LowSideOutlier = new MediCalOutlierTier(5000m, 0.60m) });

        // Cost 10,000 × 0.2 = 2,000; gain 8,000; −(8,000 − 5,000) × 0.6 = −1,800.
        var price = pricer.Price(Stay(charges: 10000m), Weight(), Hospital(ccr: 0.2m));

        Assert.Equal(MedicaidOutlierKind.LowSideCostOutlier, price.OutlierKind);
        Assert.Equal(8200m, price.AllowedAmount);
    }

    [Fact]
    public void Missing_inputs_are_unpriced_not_zero()
    {
        Assert.False(_pricer.Price(Stay(), null, Hospital()).Priced);
        Assert.False(_pricer.Price(Stay(), Weight(), Hospital(ccr: null)).Priced);
        Assert.False(_pricer.Price(Stay(), Weight(), Hospital(wage: 1.1m)).Priced); // no labor share
        Assert.False(_pricer.Price(Stay("194"), Weight("194"), Hospital()).Priced); // not a DRG-SOI key
        Assert.False(_pricer.Price(Stay(status: "02"), Weight(), Hospital()).Priced); // transfer, no ALOS
    }
}
