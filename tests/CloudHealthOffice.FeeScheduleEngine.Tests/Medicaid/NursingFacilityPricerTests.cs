using CloudHealthOffice.FeeScheduleEngine.Medicaid;

namespace CloudHealthOffice.FeeScheduleEngine.Tests.Medicaid;

/// <summary>
/// Nursing facility per diem net of patient liability: Texas applied income,
/// Medi-Cal share of cost.
/// </summary>
public class NursingFacilityPricerTests
{
    private const string Facility = "1336177138";
    private static readonly DateOnly Sept = new(2026, 9, 1);
    private static readonly DateOnly Oct = new(2026, 10, 1);

    private static NursingFacilityRate Rate(decimal daily, string? classification = null) => new()
    {
        Npi = Facility, Classification = classification, DailyRate = daily, EffectiveFrom = new DateOnly(2026, 1, 1),
    };

    private static NursingFacilityLine Days(int n, DateOnly from, int days, string rev = "0120") => new()
    {
        LineNumber = n, RevenueCode = rev, ServiceDate = from, Days = days,
    };

    private static NursingFacilityLine Hipps(int n, string code) => new()
    {
        LineNumber = n, RevenueCode = "0022", ProcedureCode = code, ServiceDate = Sept, Days = 0,
    };

    private static NursingFacilityClaim Claim(
        Dictionary<DateOnly, decimal> liability, Dictionary<DateOnly, decimal>? applied = null, params NursingFacilityLine[] lines) => new()
    {
        FacilityNpi = Facility, Lines = lines, MonthlyLiability = liability,
        LiabilityAlreadyApplied = applied ?? new Dictionary<DateOnly, decimal>(),
    };

    [Fact]
    public void Texas_pays_the_classification_rate_less_applied_income()
    {
        var pricer = new NursingFacilityPricer(NursingFacilityPolicy.Texas());
        var claim = Claim(new() { [Sept] = 1200m }, null, Hipps(1, "KBAA1"), Days(2, Sept, 30));

        var price = pricer.Price(claim, [Rate(150m, "KBAA1"), Rate(999m, "OTHER")]);

        Assert.True(price.Priced);
        Assert.Equal(4500m, price.TotalGross);           // 30 × 150
        Assert.Equal(1200m, price.TotalLiabilityApplied);
        Assert.Equal(3300m, price.TotalAllowed);
        Assert.Equal(1200m, price.LiabilityAppliedByMonth[Sept]);
    }

    [Fact]
    public void Texas_needs_the_classification_on_the_claim()
    {
        var pricer = new NursingFacilityPricer(NursingFacilityPolicy.Texas());

        Assert.False(pricer.Price(Claim(new() { [Sept] = 0m }, null, Days(1, Sept, 30)), [Rate(150m, "KBAA1")]).Priced);
        Assert.False(pricer.Price(Claim(new() { [Sept] = 0m }, null, Hipps(1, "KBAA1"), Hipps(2, "KBAB1"), Days(3, Sept, 30)),
            [Rate(150m, "KBAA1")]).Priced);
    }

    [Fact]
    public void Medi_cal_pays_the_facility_per_diem_less_share_of_cost()
    {
        var pricer = new NursingFacilityPricer(NursingFacilityPolicy.California());

        var price = pricer.Price(Claim(new() { [Sept] = 900m }, null, Days(1, Sept, 30)), [Rate(280m)]);

        Assert.Equal(8400m, price.TotalGross);
        Assert.Equal(7500m, price.TotalAllowed);
    }

    [Fact]
    public void Liability_already_met_on_an_earlier_claim_is_not_taken_twice()
    {
        var pricer = new NursingFacilityPricer(NursingFacilityPolicy.California());

        var price = pricer.Price(
            Claim(new() { [Sept] = 900m }, new() { [Sept] = 600m }, Days(1, new DateOnly(2026, 9, 16), 15)), [Rate(280m)]);

        Assert.Equal(300m, price.TotalLiabilityApplied);
        Assert.Equal(3900m, price.TotalAllowed); // 15 × 280 − 300
    }

    [Fact]
    public void Liability_larger_than_the_month_pays_nothing_and_never_goes_negative()
    {
        var pricer = new NursingFacilityPricer(NursingFacilityPolicy.California());

        var price = pricer.Price(Claim(new() { [Sept] = 5000m }, null, Days(1, new DateOnly(2026, 9, 28), 3)), [Rate(280m)]);

        Assert.Equal(840m, price.TotalLiabilityApplied);
        Assert.Equal(0m, price.TotalAllowed);
    }

    [Fact]
    public void Each_month_meets_its_own_liability()
    {
        var pricer = new NursingFacilityPricer(NursingFacilityPolicy.California());

        var price = pricer.Price(
            Claim(new() { [Sept] = 900m, [Oct] = 950m }, null, Days(1, new DateOnly(2026, 9, 21), 10), Days(2, Oct, 10)),
            [Rate(280m)]);

        Assert.Equal(1900m, price.Lines.Single(l => l.LineNumber == 1).Allowed); // 2,800 − 900
        Assert.Equal(1850m, price.Lines.Single(l => l.LineNumber == 2).Allowed); // 2,800 − 950
    }

    [Fact]
    public void Medi_cal_pays_a_bed_hold_for_up_to_seven_days()
    {
        var pricer = new NursingFacilityPricer(NursingFacilityPolicy.California());

        var price = pricer.Price(
            Claim(new() { [Sept] = 0m }, null, Days(1, Sept, 10), Days(2, new DateOnly(2026, 9, 11), 9, rev: "0185")),
            [Rate(280m)]);

        var hold = price.Lines.Single(l => l.LineNumber == 2);
        Assert.Equal(7, hold.PaidDays);
        Assert.Equal(1960m, hold.Allowed);
    }

    [Fact]
    public void Leave_with_no_configured_rule_pends()
    {
        var pricer = new NursingFacilityPricer(NursingFacilityPolicy.Texas());

        var price = pricer.Price(
            Claim(new() { [Sept] = 0m }, null, Hipps(1, "KBAA1"), Days(2, Sept, 3, rev: "0183")), [Rate(150m, "KBAA1")]);

        Assert.False(price.Priced);
    }

    [Fact]
    public void Missing_liability_rates_or_month_splits_pend()
    {
        var pricer = new NursingFacilityPricer(NursingFacilityPolicy.California());

        Assert.False(pricer.Price(Claim(new(), null, Days(1, Sept, 30)), [Rate(280m)]).Priced);          // no liability on file
        Assert.False(pricer.Price(Claim(new() { [Sept] = 0m }, null, Days(1, Sept, 30)), []).Priced);    // no rate
        Assert.False(pricer.Price(Claim(new() { [Sept] = 0m }, null, Days(1, new DateOnly(2026, 9, 25), 10)), [Rate(280m)]).Priced); // spans months
        Assert.False(pricer.Price(Claim(new() { [Sept] = 0m }, null, Days(1, Sept, 5, rev: "0250")), [Rate(280m)]).Priced);          // ancillary
    }
}
