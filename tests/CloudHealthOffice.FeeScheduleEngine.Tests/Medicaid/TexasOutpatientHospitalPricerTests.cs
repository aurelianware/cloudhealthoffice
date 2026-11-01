using CloudHealthOffice.FeeScheduleEngine.Medicaid;

namespace CloudHealthOffice.FeeScheduleEngine.Tests.Medicaid;

/// <summary>Texas Medicaid outpatient hospital, 1 TAC §355.8061, both methods.</summary>
public class TexasOutpatientHospitalPricerTests
{
    private static TexasOutpatientHospital Hospital(
        TexasHospitalClass cls = TexasHospitalClass.Urban, bool highVolume = true, bool stateOwned = false,
        decimal? interim = 0.40m, decimal? cf = null) => new()
    {
        Npi = "1234567893", Class = cls, HighVolume = highVolume, StateOwned = stateOwned, InterimRate = interim,
        EapgConversionFactor = cf,
    };

    private static TexasOutpatientLine Line(int n, decimal charges, string? eapg = null, bool payable = true, decimal factor = 1m) => new()
    {
        LineNumber = n, AllowableCharges = charges, Eapg = eapg, EapgPayable = payable, EapgDiscountFactor = factor,
    };

    private readonly TexasOutpatientHospitalPricer _interim = new(TexasOutpatientMethod.PercentOfChargesInterim);

    [Theory]
    [InlineData(TexasHospitalClass.Urban, true, false, 288.00)]      // 1,000 × 72.00% × 0.40
    [InlineData(TexasHospitalClass.Urban, false, false, 273.76)]     // 68.44%
    [InlineData(TexasHospitalClass.Childrens, true, false, 304.12)]  // 76.03%
    [InlineData(TexasHospitalClass.Childrens, false, false, 289.08)] // 72.27%
    [InlineData(TexasHospitalClass.Urban, true, true, 304.12)]       // state-owned shares children's
    [InlineData(TexasHospitalClass.Rural, false, false, 400.00)]     // 100%
    public void The_interim_method_pays_class_percentage_of_charges_times_interim_rate(
        TexasHospitalClass cls, bool highVolume, bool stateOwned, double expected)
    {
        var price = _interim.Price(Hospital(cls, highVolume, stateOwned), [Line(1, 1000m)]);

        Assert.True(price.Priced);
        Assert.Equal((decimal)expected, price.TotalAllowed);
    }

    [Fact]
    public void A_rural_interim_rate_above_100_percent_is_capped()
    {
        var price = _interim.Price(Hospital(TexasHospitalClass.Rural, interim: 1.2m), [Line(1, 1000m)]);

        Assert.Equal(1000m, price.TotalAllowed);
    }

    [Fact]
    public void The_interim_method_needs_an_interim_rate()
    {
        Assert.False(_interim.Price(Hospital(interim: null), [Line(1, 1000m)]).Priced);
    }

    [Fact]
    public void The_eapg_method_pays_weight_times_conversion_factor_times_discount()
    {
        var pricer = new TexasOutpatientHospitalPricer(TexasOutpatientMethod.Eapg);
        var weights = new Dictionary<string, decimal> { ["00096"] = 2.5m, ["00097"] = 1.2m };

        var price = pricer.Price(
            Hospital(cf: 400m),
            [Line(1, 900m, "00096"), Line(2, 500m, "00097", factor: 0.5m), Line(3, 120m, "00421", payable: false)],
            weights);

        Assert.True(price.Priced);
        Assert.Equal(1000m, price.LineAllowed[1]); // 2.5 × 400
        Assert.Equal(240m, price.LineAllowed[2]);  // 1.2 × 400 × 0.5
        Assert.Equal(0m, price.LineAllowed[3]);    // packaged
    }

    [Fact]
    public void The_eapg_method_pends_on_missing_inputs()
    {
        var pricer = new TexasOutpatientHospitalPricer(TexasOutpatientMethod.Eapg);
        var weights = new Dictionary<string, decimal> { ["00096"] = 2.5m };

        Assert.False(pricer.Price(Hospital(cf: null), [Line(1, 900m, "00096")], weights).Priced);
        Assert.False(pricer.Price(Hospital(cf: 400m), [Line(1, 900m, "00096")], null).Priced);
        Assert.False(pricer.Price(Hospital(cf: 400m), [Line(1, 900m, "99999")], weights).Priced);
        Assert.False(pricer.Price(Hospital(cf: 400m), [Line(1, 900m, "00096", factor: 0m)], weights).Priced);
    }
}
