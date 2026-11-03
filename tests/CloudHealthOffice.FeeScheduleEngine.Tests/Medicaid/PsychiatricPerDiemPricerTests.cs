using CloudHealthOffice.FeeScheduleEngine.Medicaid;

namespace CloudHealthOffice.FeeScheduleEngine.Tests.Medicaid;

/// <summary>Texas psychiatric hospital per diem with the adult IMD monthly day limit.</summary>
public class PsychiatricPerDiemPricerTests
{
    // Cypress Creek Hospital, SFY 2027 per diem $563.36 (HHSC, public).
    private const string Hospital = "1730187568";
    private static readonly PsychiatricPerDiemRate Sfy2027 = new()
    {
        Npi = Hospital, DailyRate = 563.36m, EffectiveFrom = new DateOnly(2026, 9, 1), Source = "SFY 2027",
    };

    private static PsychiatricStayLine Days(int n, DateOnly from, int days, string rev = "0114") => new()
    {
        LineNumber = n, RevenueCode = rev, ServiceDate = from, Days = days,
    };

    private static PsychiatricStayLine Ancillary(int n, string rev = "0250") => new()
    {
        LineNumber = n, RevenueCode = rev, ServiceDate = new DateOnly(2026, 10, 1), ChargeAmount = 400m,
    };

    private static PsychiatricStayClaim Claim(int? age, params PsychiatricStayLine[] lines) => new()
    {
        FacilityNpi = Hospital, AgeAtAdmission = age, Lines = lines,
    };

    private static readonly PsychiatricPerDiemPricer Texas = new(PsychiatricPerDiemPolicy.Texas());

    [Fact]
    public void Pays_days_times_the_per_diem_and_ancillaries_at_zero()
    {
        var price = Texas.Price(Claim(16, Days(1, new DateOnly(2026, 10, 1), 7), Ancillary(2)), [Sfy2027]);

        Assert.True(price.Priced);
        Assert.Equal(3943.52m, price.TotalAllowed); // 7 × 563.36
        Assert.Equal(0m, price.Lines.Single(l => l.LineNumber == 2).Allowed);
        Assert.Contains(price.Steps, s => s.Contains("does not apply"));
    }

    [Fact]
    public void An_adult_within_fifteen_days_a_month_is_paid()
    {
        var price = Texas.Price(Claim(35, Days(1, new DateOnly(2026, 10, 1), 15)), [Sfy2027]);

        Assert.True(price.Priced);
        Assert.Equal(15, price.ImdDaysByMonth[new DateOnly(2026, 10, 1)]);
    }

    [Fact]
    public void An_adult_over_fifteen_days_in_a_month_pends()
    {
        var price = Texas.Price(Claim(35, Days(1, new DateOnly(2026, 10, 1), 16)), [Sfy2027]);

        Assert.False(price.Priced);
        Assert.Contains("15-day monthly limit", price.UnpricedReason);
        Assert.Contains("Pended", price.UnpricedReason);
    }

    [Fact]
    public void A_stay_split_across_months_is_counted_per_month()
    {
        // Oct 20 - Nov 8: 12 days in October, 8 in November.
        var price = Texas.Price(Claim(40, Days(1, new DateOnly(2026, 10, 20), 20)), [Sfy2027]);

        Assert.True(price.Priced);
        Assert.Equal(12, price.ImdDaysByMonth[new DateOnly(2026, 10, 1)]);
        Assert.Equal(8, price.ImdDaysByMonth[new DateOnly(2026, 11, 1)]);
    }

    [Fact]
    public void Days_on_earlier_claims_count_toward_the_month()
    {
        var claim = Claim(40, Days(1, new DateOnly(2026, 10, 20), 5)) with
        {
            ImdDaysAlreadyPaid = new Dictionary<DateOnly, int> { [new DateOnly(2026, 10, 1)] = 11 },
        };

        Assert.False(Texas.Price(claim, [Sfy2027]).Priced);
    }

    [Fact]
    public void The_plan_can_choose_to_pay_over_the_limit()
    {
        var pricer = new PsychiatricPerDiemPricer(PsychiatricPerDiemPolicy.Texas() with { OverLimitAction = ImdOverLimitAction.Pay });

        var price = pricer.Price(Claim(35, Days(1, new DateOnly(2026, 10, 1), 20)), [Sfy2027]);

        Assert.True(price.Priced);
        Assert.Equal(11267.20m, price.TotalAllowed);
        Assert.Contains(price.Steps, s => s.Contains("Paid under the plan's setting"));
    }

    [Theory]
    [InlineData(20)]
    [InlineData(65)]
    public void Under_21_and_65_or_older_have_no_limit(int age)
    {
        Assert.True(Texas.Price(Claim(age, Days(1, new DateOnly(2026, 10, 1), 30)), [Sfy2027]).Priced);
    }

    [Fact]
    public void An_unknown_age_pends()
    {
        Assert.Contains("age at admission is unknown", Texas.Price(Claim(null, Days(1, new DateOnly(2026, 10, 1), 3)), [Sfy2027]).UnpricedReason);
    }

    [Fact]
    public void No_rate_for_the_dates_pends()
    {
        Assert.Contains("No psychiatric per diem", Texas.Price(Claim(16, Days(1, new DateOnly(2026, 8, 30), 3)), [Sfy2027]).UnpricedReason);
    }

    [Fact]
    public void Leave_days_pend()
    {
        Assert.Contains("leave", Texas.Price(Claim(16, Days(1, new DateOnly(2026, 10, 1), 3), Days(2, new DateOnly(2026, 10, 4), 1, "0183")), [Sfy2027]).UnpricedReason);
    }

    [Fact]
    public void A_claim_with_only_ancillaries_pends()
    {
        Assert.Contains("no accommodation", Texas.Price(Claim(16, Ancillary(1)), [Sfy2027]).UnpricedReason);
    }
}
