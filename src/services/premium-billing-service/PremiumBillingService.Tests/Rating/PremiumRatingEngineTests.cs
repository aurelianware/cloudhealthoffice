using PremiumBillingService.Rating;
using static PremiumBillingService.Tests.Rating.RatingFixtures;

namespace PremiumBillingService.Tests.Rating;

public class AgeCurveTests
{
    [Theory]
    [InlineData(0, 0.765)]
    [InlineData(14, 0.765)]
    [InlineData(15, 0.833)]
    [InlineData(20, 0.970)]
    [InlineData(21, 1.000)]
    [InlineData(40, 1.278)]
    [InlineData(45, 1.444)]
    [InlineData(63, 2.952)]
    [InlineData(64, 3.000)]
    [InlineData(90, 3.000)]
    public void FederalDefault_IsLoadedFromData(int age, double factor)
    {
        AgeCurve.FederalDefault.FactorFor(age).Should().Be((decimal)factor);
    }

    [Fact]
    public void FederalDefault_HasTheThreeToOneAdultRatio()
    {
        var curve = AgeCurve.FederalDefault;
        (curve.FactorFor(64) / curve.FactorFor(21)).Should().Be(3m);
        curve.Problems().Should().BeEmpty();
    }

    [Fact]
    public void FromJson_LoadsAStateCurve()
    {
        var curve = AgeCurve.FromJson("""
            { "name": "Flat community rating", "bands": [ { "minAge": 0, "maxAge": 20, "factor": 0.5 }, { "minAge": 21, "maxAge": null, "factor": 1.0 } ] }
            """);

        curve.FactorFor(10).Should().Be(0.5m);
        curve.FactorFor(70).Should().Be(1.0m);
    }

    [Fact]
    public void FromJson_RejectsAnAdultRatioAboveThreeToOne()
    {
        var act = () => AgeCurve.FromJson("""
            { "name": "too steep", "bands": [ { "minAge": 0, "maxAge": 20, "factor": 0.5 }, { "minAge": 21, "maxAge": 63, "factor": 1.0 }, { "minAge": 64, "maxAge": null, "factor": 3.01 } ] }
            """);

        act.Should().Throw<FormatException>().WithMessage("*more than 3.0:1*");
    }

    [Fact]
    public void FromJson_RejectsAGappedCurve()
    {
        var act = () => AgeCurve.FromJson("""
            { "name": "bad", "bands": [ { "minAge": 0, "maxAge": 20, "factor": 0.5 }, { "minAge": 22, "maxAge": null, "factor": 1.0 } ] }
            """);

        act.Should().Throw<FormatException>().WithMessage("*gap or overlap after age 20*");
    }
}

public class TierRatingTests
{
    [Theory]
    [InlineData(false, false, CoverageTier.EmployeeOnly, 500)]
    [InlineData(true, false, CoverageTier.EmployeeSpouse, 1000)]
    [InlineData(false, true, CoverageTier.EmployeeChildren, 900)]
    [InlineData(true, true, CoverageTier.Family, 1400)]
    public void Tier_IsDerivedFromMembers_AndPriced(bool spouse, bool child, CoverageTier tier, int expected)
    {
        var members = new List<RatedMember> { Subscriber("S") };
        if (spouse) members.Add(Member("SP", MemberRelationship.Spouse, D(1986, 1, 1)));
        if (child) members.Add(Member("C1", MemberRelationship.Child, D(2015, 1, 1)));

        var result = PremiumRatingEngine.Rate(TierTable(),
            Household("cov-1", "PPO-GOLD", PlanYearStart, null, members.ToArray()));

        result.Tier.Should().Be(tier);
        result.MonthlyPremium.Should().Be(expected);
        result.Members.Single(m => m.Relationship == MemberRelationship.Subscriber).BasePremium.Should().Be(expected);
    }

    [Fact]
    public void Tier_ExplicitTierWins()
    {
        var household = Household("cov-1", "PPO-GOLD", PlanYearStart, null, Subscriber("S"));
        household.Tier = CoverageTier.Family;

        PremiumRatingEngine.Rate(TierTable(), household).MonthlyPremium.Should().Be(1400m);
    }

    [Fact]
    public void Tier_TobaccoFactor_ChargesFactorTimesSingleRatePerAdultUser()
    {
        // 1.2 × EE 500 → 100.00 per tobacco user of age; the 19-year-old is under the tobacco age.
        var table = TierTable(tobacco: new TobaccoSurcharge { Factor = 1.2m });
        var household = Household("cov-1", "PPO-GOLD", PlanYearStart, null,
            Subscriber("S", tobacco: true),
            Member("SP", MemberRelationship.Spouse, D(1986, 1, 1), tobacco: true),
            Member("C1", MemberRelationship.Child, D(2006, 6, 1), tobacco: true));

        var result = PremiumRatingEngine.Rate(table, household);

        result.BasePremium.Should().Be(1400m);
        result.TobaccoSurcharge.Should().Be(200m);
        result.MonthlyPremium.Should().Be(1600m);
    }

    [Fact]
    public void Tier_TobaccoFlatAmount()
    {
        var table = TierTable(tobacco: new TobaccoSurcharge { Factor = 1.5m, FlatMonthlyAmount = 35m });
        var result = PremiumRatingEngine.Rate(table,
            Household("cov-1", "PPO-GOLD", PlanYearStart, null, Subscriber("S", tobacco: true)));

        result.MonthlyPremium.Should().Be(535m);
    }

    [Fact]
    public void Rate_RejectsAPlanMismatch()
    {
        var act = () => PremiumRatingEngine.Rate(TierTable(), Household("cov-1", "OTHER", PlanYearStart, null, Subscriber("S")));
        act.Should().Throw<ArgumentException>();
    }
}

public class AgeBandRatingTests
{
    // Ages measured on 2026-01-01 (plan-year start):
    // S 45 (1.444, tobacco), SP 43 (1.357), children 19 (0.941), 16 (0.859), 12 (0.765), 8 (0.765).
    private static RatingEnrollment FamilyOfSix(params RatedMember[] extra)
    {
        var members = new List<RatedMember>
        {
            Subscriber("S", D(1980, 6, 15), tobacco: true),
            Member("SP", MemberRelationship.Spouse, D(1982, 3, 1)),
            Member("C19", MemberRelationship.Child, D(2006, 5, 1)),
            Member("C16", MemberRelationship.Child, D(2009, 7, 1)),
            Member("C12", MemberRelationship.Child, D(2013, 2, 1)),
            Member("C8", MemberRelationship.Child, D(2017, 9, 1)),
        };
        members.AddRange(extra);
        return Household("cov-fam", "HMO-SILVER", PlanYearStart, null, members.ToArray());
    }

    [Fact]
    public void AgeBand_SingleAdult_IsBaseRateTimesFactor()
    {
        var result = PremiumRatingEngine.Rate(AgeBandTable(),
            Household("cov-1", "HMO-SILVER", PlanYearStart, null, Subscriber("S", D(1966, 1, 1)))); // 60 → 2.714

        result.MonthlyPremium.Should().Be(814.20m);
        result.Members.Single().AgeFactor.Should().Be(2.714m);
    }

    [Fact]
    public void AgeBand_Family_HandChecked_ThreeOldestChildrenUnder21_AndTobacco()
    {
        var table = AgeBandTable(tobacco: new TobaccoSurcharge { Factor = 1.20m });

        var result = PremiumRatingEngine.Rate(table, FamilyOfSix());

        // S   300 × 1.444 = 433.20 + tobacco 433.20 × 0.20 = 86.64 → 519.84
        // SP  300 × 1.357 = 407.10
        // C19 300 × 0.941 = 282.30
        // C16 300 × 0.859 = 257.70
        // C12 300 × 0.765 = 229.50
        // C8  fourth child under 21: not rated
        // Total 1,696.44
        result.Tier.Should().Be(CoverageTier.Family);
        Premium(result, "S").Should().Be(519.84m);
        result.Members.Single(m => m.MemberId == "S").TobaccoSurcharge.Should().Be(86.64m);
        Premium(result, "SP").Should().Be(407.10m);
        Premium(result, "C19").Should().Be(282.30m);
        Premium(result, "C16").Should().Be(257.70m);
        Premium(result, "C12").Should().Be(229.50m);
        var c8 = result.Members.Single(m => m.MemberId == "C8");
        c8.Rated.Should().BeFalse();
        c8.Premium.Should().Be(0m);
        result.TobaccoSurcharge.Should().Be(86.64m);
        result.MonthlyPremium.Should().Be(1696.44m);
    }

    [Fact]
    public void AgeBand_ChildrenAged21AndOver_AreAlwaysRated_AndDoNotUseACapSlot()
    {
        // C23 (factor 1.000 → 300.00) is rated on top of the three oldest under 21.
        var result = PremiumRatingEngine.Rate(AgeBandTable(), FamilyOfSix(Member("C23", MemberRelationship.Child, D(2002, 4, 1))));

        Premium(result, "C23").Should().Be(300.00m);
        result.Members.Where(m => m.Rated).Select(m => m.MemberId)
            .Should().BeEquivalentTo("S", "SP", "C23", "C19", "C16", "C12");
        // 433.20 + 407.10 + 300.00 + 282.30 + 257.70 + 229.50
        result.MonthlyPremium.Should().Be(1909.80m);
    }

    [Fact]
    public void AgeBand_TobaccoIsNotAppliedUnderTheMinimumAge()
    {
        var table = AgeBandTable(tobacco: new TobaccoSurcharge { Factor = 1.5m, MinimumAge = 21 });
        var result = PremiumRatingEngine.Rate(table, Household("cov-1", "HMO-SILVER", PlanYearStart, null,
            Subscriber("S", D(2006, 1, 1), tobacco: true))); // 20 → 0.970

        result.MonthlyPremium.Should().Be(291.00m);
        result.TobaccoSurcharge.Should().Be(0m);
    }

    [Fact]
    public void AgeBand_AgeIsFixedAtThePlanYearStart_OrALaterCoverageStart()
    {
        // Turns 64 on 2026-03-01: 63 (2.952) at the plan-year start, 64 (3.000) for a June join.
        var dob = D(1962, 3, 1);
        var renewal = PremiumRatingEngine.Rate(AgeBandTable(), Household("c1", "HMO-SILVER", PlanYearStart, null, Subscriber("S", dob)));
        var joinedJune = PremiumRatingEngine.Rate(AgeBandTable(), Household("c2", "HMO-SILVER", D(2026, 6, 1), null, Subscriber("S", dob)));

        renewal.MonthlyPremium.Should().Be(885.60m);
        joinedJune.MonthlyPremium.Should().Be(900.00m);
    }

    [Fact]
    public void AgeBand_ADependentAddedMidYear_IsAgedOnTheirOwnStart()
    {
        // Spouse born 1981-03-01: 44 (1.397) on 1/1, 45 (1.444 → 433.20) when added on 6/1.
        var spouse = Member("SP", MemberRelationship.Spouse, D(1981, 3, 1));
        spouse.EffectiveDate = D(2026, 6, 1);
        var household = Household("c1", "HMO-SILVER", PlanYearStart, null, Subscriber("S", D(1996, 1, 1)), spouse); // S 30 → 1.135

        var result = PremiumRatingEngine.RateOn(AgeBandTable(), household, D(2026, 6, 1));

        result.Members.Single(m => m.MemberId == "SP").Premium.Should().Be(433.20m);
        result.Members.Single(m => m.MemberId == "S").Premium.Should().Be(340.50m);
        PremiumRatingEngine.RateOn(AgeBandTable(), household, D(2026, 5, 31)).Members.Should().ContainSingle();
    }

    [Fact]
    public void AgeBand_UsesTheTablesOwnCurveWhenGiven()
    {
        var table = AgeBandTable();
        table.AgeCurve = AgeCurve.FromJson("""{ "name": "flat", "bands": [ { "minAge": 0, "maxAge": null, "factor": 1.0 } ] }""");

        PremiumRatingEngine.Rate(table, Household("c1", "HMO-SILVER", PlanYearStart, null, Subscriber("S", D(1960, 1, 1))))
            .MonthlyPremium.Should().Be(300m);
    }

    private static decimal Premium(RatingResult result, string memberId) => result.Members.Single(m => m.MemberId == memberId).Premium;
}

public class CompositeRatingTests
{
    // Census on the age-band table (base 300): H1 single age 21 → 300.00;
    // H2 subscriber 64 (3.000 → 900.00) + spouse 60 (2.714 → 814.20). Total 2,014.20 over 3 members.
    private static List<RatingEnrollment> Census() => new()
    {
        Household("h1", "HMO-SILVER", PlanYearStart, null, Subscriber("A", D(2004, 6, 1))),
        Household("h2", "HMO-SILVER", PlanYearStart, null,
            Subscriber("B", D(1961, 6, 1), tobacco: true),
            Member("B-SP", MemberRelationship.Spouse, D(1965, 6, 1))),
    };

    private static RateTable Composite(decimal rate, CompositeBasis basis = CompositeBasis.PerMember, TierAmounts? factors = null,
        TobaccoSurcharge? tobacco = null) => new()
    {
        Id = "rt-comp",
        PlanId = "HMO-SILVER",
        EffectiveFrom = PlanYearStart,
        EffectiveTo = PlanYearEnd,
        Method = RatingMethod.Composite,
        CompositeRate = rate,
        CompositeBasis = basis,
        CompositeTierFactors = factors,
        Tobacco = tobacco
    };

    [Fact]
    public void DeriveCompositeRate_PerMember_IsAgeRatedTotalOverMembers_ExcludingTobacco()
    {
        var table = AgeBandTable(tobacco: new TobaccoSurcharge { Factor = 1.5m });

        PremiumRatingEngine.DeriveCompositeRate(table, Census(), CompositeBasis.PerMember)
            .Should().Be(671.40m); // 2,014.20 / 3
    }

    [Fact]
    public void Composite_PerMember_ChargesRateTimesRatedMembers()
    {
        var result = PremiumRatingEngine.Rate(Composite(671.40m), Census()[1]);

        result.MonthlyPremium.Should().Be(1342.80m);
    }

    [Fact]
    public void Composite_PerMember_AppliesTheChildCap()
    {
        var household = Household("h3", "HMO-SILVER", PlanYearStart, null,
            Subscriber("S"),
            Member("C1", MemberRelationship.Child, D(2010, 1, 1)),
            Member("C2", MemberRelationship.Child, D(2012, 1, 1)),
            Member("C3", MemberRelationship.Child, D(2014, 1, 1)),
            Member("C4", MemberRelationship.Child, D(2016, 1, 1)));

        var result = PremiumRatingEngine.Rate(Composite(100m), household);

        result.MonthlyPremium.Should().Be(400m);
        result.Members.Single(m => m.MemberId == "C4").Rated.Should().BeFalse();
    }

    [Fact]
    public void Composite_TierFactors_HandChecked()
    {
        var factors = new TierAmounts { EmployeeOnly = 1.0m, EmployeeSpouse = 1.9m, EmployeeChildren = 1.8m, Family = 2.9m };

        var rate = PremiumRatingEngine.DeriveCompositeRate(AgeBandTable(), Census(), CompositeBasis.TierFactors, factors);
        rate.Should().Be(694.55m); // 2,014.20 / (1.0 + 1.9) = 694.5517…

        var es = PremiumRatingEngine.Rate(Composite(rate, CompositeBasis.TierFactors, factors), Census()[1]);
        es.Tier.Should().Be(CoverageTier.EmployeeSpouse);
        es.MonthlyPremium.Should().Be(1319.65m); // 694.55 × 1.9 = 1,319.645 → half away from zero
    }

    [Fact]
    public void Composite_TobaccoIsRatedOnTheUsersAgeRatedPremium()
    {
        // B is 64: age-rated 300 × 3.000 = 900.00; 900.00 × 0.10 = 90.00 on top of 2 × 671.40.
        var table = Composite(671.40m, tobacco: new TobaccoSurcharge { Factor = 1.1m });
        table.AgeBandBaseRate = 300m;

        var result = PremiumRatingEngine.Rate(table, Census()[1]);

        result.TobaccoSurcharge.Should().Be(90.00m);
        result.MonthlyPremium.Should().Be(1432.80m);
    }

    [Fact]
    public void Composite_TobaccoNeedsTheAgeBandBaseRate()
    {
        Composite(671.40m, tobacco: new TobaccoSurcharge { Factor = 1.1m })
            .Invoking(t => t.Validate()).Should().Throw<RateTableValidationException>().WithMessage("*AgeBandBaseRate*");
    }
}

public class RateTableTests
{
    [Fact]
    public void Validate_RejectsATobaccoFactorAboveOnePointFive()
    {
        var table = TierTable(tobacco: new TobaccoSurcharge { Factor = 1.6m });
        table.Invoking(t => t.Validate()).Should().Throw<RateTableValidationException>().WithMessage("*between 1.0 and 1.5*");
    }

    [Fact]
    public void Validate_RejectsAFlatTobaccoAmountAboveHalfTheEeRate()
    {
        // EE 500: at most 250.00 a month.
        TierTable(tobacco: new TobaccoSurcharge { Factor = 1.5m, FlatMonthlyAmount = 250.01m })
            .Invoking(t => t.Validate()).Should().Throw<RateTableValidationException>().WithMessage("*1.5:1*");
        TierTable(tobacco: new TobaccoSurcharge { Factor = 1.5m, FlatMonthlyAmount = 250m }).Validate();
    }

    [Fact]
    public void Validate_RejectsAFlatTobaccoAmountOnAcaMethods()
    {
        AgeBandTable(tobacco: new TobaccoSurcharge { Factor = 1.2m, FlatMonthlyAmount = 20m })
            .Invoking(t => t.Validate()).Should().Throw<RateTableValidationException>().WithMessage("*tier tables only*");
    }

    [Fact]
    public void Validate_RequiresTheRatesOfTheMethod()
    {
        new RateTable { PlanId = "P", Method = RatingMethod.AgeBand, EffectiveFrom = PlanYearStart }
            .Invoking(t => t.Validate()).Should().Throw<RateTableValidationException>().WithMessage("*AgeBandBaseRate*");
        new RateTable { PlanId = "P", Method = RatingMethod.Composite, EffectiveFrom = PlanYearStart }
            .Invoking(t => t.Validate()).Should().Throw<RateTableValidationException>().WithMessage("*CompositeRate*");
    }

    [Fact]
    public void Catalog_ResolvesByPlanAndDate()
    {
        var y2026 = TierTable();
        var y2027 = TierTable();
        y2027.Id = "rt-2027";
        y2027.EffectiveFrom = D(2027, 1, 1);
        y2027.EffectiveTo = null;
        var catalog = new RateTableCatalog(new[] { y2027, y2026 });

        catalog.Resolve("ppo-gold", D(2026, 12, 31)).Should().BeSameAs(y2026);
        catalog.Resolve("PPO-GOLD", D(2030, 1, 1)).Should().BeSameAs(y2027);
        catalog.Invoking(c => c.Resolve("PPO-GOLD", D(2025, 12, 31))).Should().Throw<RateTableNotFoundException>();
    }

    [Fact]
    public void Catalog_RejectsOverlappingPeriods()
    {
        var a = TierTable();
        var b = TierTable();
        b.EffectiveFrom = D(2026, 7, 1);

        var act = () => new RateTableCatalog(new[] { a, b });
        act.Should().Throw<RateTableValidationException>().WithMessage("*overlap*");
    }
}
