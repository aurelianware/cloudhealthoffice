using CloudHealthOffice.FeeScheduleEngine.Medicaid;

namespace CloudHealthOffice.FeeScheduleEngine.Tests.Medicaid;

/// <summary>
/// FQHC/RHC encounter pricing for Medicaid managed care, Texas and Medi-Cal.
/// </summary>
public class ClinicEncounterPricerTests
{
    private const string Clinic = "1497758544";
    private static readonly DateOnly Day = new(2026, 9, 15);

    private static readonly ClinicEncounterRate Pps = new()
    {
        Npi = Clinic, Kind = ClinicKind.Fqhc, PerVisitRate = 250m, EffectiveFrom = new DateOnly(2026, 1, 1),
    };

    private static ClinicClaimLine Line(int n, string code, decimal? contracted, DateOnly? date = null,
        string? rev = null, params string[] modifiers) => new()
    {
        LineNumber = n, ProcedureCode = code, ContractedAllowed = contracted, ServiceDate = date ?? Day,
        RevenueCode = rev, Modifiers = modifiers, ChargeAmount = 300m,
    };

    private static ClinicClaim Claim(bool stateOnly = false, params ClinicClaimLine[] lines) => new()
    {
        ClinicNpi = Clinic, Lines = lines, StateOnlyServices = stateOnly,
    };

    [Fact]
    public void Texas_plan_pays_its_contract_for_the_visit_and_records_the_wrap_to_pps()
    {
        var pricer = new ClinicEncounterPricer(ClinicEncounterPolicy.Texas());

        var price = pricer.Price(Claim(false, Line(1, "T1015", 20m), Line(2, "99213", 75m), Line(3, "85025", 10m)), [Pps]);

        Assert.True(price.Priced);
        Assert.Equal(105m, price.TotalAllowed);
        var encounter = Assert.Single(price.Encounters);
        Assert.Equal(105m, encounter.PlanPayment);
        Assert.Equal(145m, encounter.ExpectedStateWrap); // 250 − 105
        Assert.True(encounter.WrapEligible);
    }

    [Fact]
    public void A_contract_above_pps_leaves_no_wrap()
    {
        var pricer = new ClinicEncounterPricer(ClinicEncounterPolicy.Texas());

        var price = pricer.Price(Claim(false, Line(1, "T1015", 300m)), [Pps]);

        Assert.Equal(0m, price.TotalExpectedStateWrap);
    }

    [Fact]
    public void When_the_plan_pays_pps_the_encounter_line_carries_the_rate_and_the_rest_is_zero()
    {
        var pricer = new ClinicEncounterPricer(ClinicEncounterPolicy.Texas() with { PaymentMode = ClinicPaymentMode.PlanPaysPps });

        var price = pricer.Price(Claim(false, Line(1, "T1015", null), Line(2, "99213", null)), [Pps]);

        Assert.True(price.Priced);
        Assert.Equal(250m, price.LineAllowed[1]);
        Assert.Equal(0m, price.LineAllowed[2]);
        Assert.Equal(0m, price.Single().ExpectedStateWrap);
    }

    [Fact]
    public void Medi_cal_state_only_services_get_no_wrap_from_july_2026()
    {
        var pricer = new ClinicEncounterPricer(ClinicEncounterPolicy.California());

        var before = pricer.Price(Claim(true, Line(1, "T1015", 100m, new DateOnly(2026, 6, 30))), [Pps]);
        var after = pricer.Price(Claim(true, Line(1, "T1015", 100m, new DateOnly(2026, 7, 1))), [Pps]);
        var federal = pricer.Price(Claim(false, Line(1, "T1015", 100m, new DateOnly(2026, 7, 1))), [Pps]);

        Assert.Equal(150m, before.TotalExpectedStateWrap);
        Assert.Equal(0m, after.TotalExpectedStateWrap);
        Assert.False(after.Encounters[0].WrapEligible);
        Assert.Equal(150m, federal.TotalExpectedStateWrap);
    }

    [Fact]
    public void A_day_without_an_encounter_line_prices_by_contract_with_no_wrap()
    {
        var pricer = new ClinicEncounterPricer(ClinicEncounterPolicy.Texas());

        var price = pricer.Price(Claim(false, Line(1, "36415", 8m)), [Pps]);

        Assert.True(price.Priced);
        Assert.Equal(8m, price.TotalAllowed);
        Assert.Empty(price.Encounters);
    }

    [Fact]
    public void Two_encounter_lines_the_same_day_earn_one_encounter()
    {
        var pricer = new ClinicEncounterPricer(ClinicEncounterPolicy.Texas());

        var price = pricer.Price(Claim(false, Line(1, "T1015", 50m), Line(2, "T1015", 50m)), [Pps]);

        var encounter = Assert.Single(price.Encounters);
        Assert.Equal(100m, encounter.PlanPayment);
        Assert.Equal(150m, encounter.ExpectedStateWrap);
    }

    [Fact]
    public void Separate_categories_the_same_day_are_separate_encounters_with_their_own_rates()
    {
        var policy = ClinicEncounterPolicy.Texas() with
        {
            CategoryByModifierOrRevenueCode = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["0512"] = "dental",
            },
        };
        var dentalRate = Pps with { PerVisitRate = 180m, Category = "dental" };
        var pricer = new ClinicEncounterPricer(policy);

        var price = pricer.Price(Claim(false,
            Line(1, "T1015", 60m), Line(2, "99213", 40m),
            Line(3, "T1015", 30m, rev: "0512"), Line(4, "D1120", 20m, rev: "0512")), [Pps, dentalRate]);

        Assert.Equal(2, price.Encounters.Count);
        var medical = price.Encounters.Single(e => e.Category == "medical");
        var dental = price.Encounters.Single(e => e.Category == "dental");
        Assert.Equal(150m, medical.ExpectedStateWrap); // 250 − (60 + 40)
        Assert.Equal(130m, dental.ExpectedStateWrap);  // 180 − (30 + 20)
    }

    [Fact]
    public void Each_service_date_is_its_own_encounter()
    {
        var pricer = new ClinicEncounterPricer(ClinicEncounterPolicy.Texas());

        var price = pricer.Price(Claim(false, Line(1, "T1015", 100m), Line(2, "T1015", 100m, Day.AddDays(7))), [Pps]);

        Assert.Equal(2, price.Encounters.Count);
        Assert.Equal(300m, price.TotalExpectedStateWrap);
    }

    [Fact]
    public void The_rate_in_force_on_the_service_date_is_used()
    {
        var old = Pps with { PerVisitRate = 200m, EffectiveTo = new DateOnly(2026, 8, 31) };
        var current = Pps with { PerVisitRate = 260m, EffectiveFrom = new DateOnly(2026, 9, 1) };
        var pricer = new ClinicEncounterPricer(ClinicEncounterPolicy.Texas());

        var price = pricer.Price(Claim(false, Line(1, "T1015", 100m)), [old, current]);

        Assert.Equal(160m, price.TotalExpectedStateWrap);
    }

    [Fact]
    public void Missing_rates_or_contract_amounts_pend_rather_than_pay()
    {
        var pricer = new ClinicEncounterPricer(ClinicEncounterPolicy.Texas());

        Assert.False(pricer.Price(Claim(false, Line(1, "T1015", 100m)), []).Priced);
        Assert.False(pricer.Price(Claim(false, Line(1, "T1015", 100m), Line(2, "99213", null)), [Pps]).Priced);
        Assert.False(pricer.Price(Claim(false, Line(1, "36415", null)), [Pps]).Priced);
        Assert.False(pricer.Price(Claim(false, Line(1, "T1015", 1m), Line(1, "99213", 1m)), [Pps]).Priced);
    }
}

internal static class ClinicPriceExtensions
{
    public static ClinicEncounter Single(this ClinicEncounterPrice price) => Assert.Single(price.Encounters);
}
