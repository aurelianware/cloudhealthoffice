using PremiumBillingService.Models;
using PremiumBillingService.Rating;
using static PremiumBillingService.Tests.Rating.RatingFixtures;

namespace PremiumBillingService.Tests.Rating;

/// <summary>
/// The calculator as rated billing uses it: rate versions on every line,
/// list vs composite presentation, proration rules, retro adjustments, rate
/// changes, missing rates and rounding. Every expected amount is worked by hand
/// in the comments.
/// </summary>
public class RatedBillingCalculatorTests
{
    private const string Plan = "PPO-GOLD"; // tier: EE 500, ES 1000, EC 900, FAM 1400

    private static RateTable Versioned(RateTable table, int version, string? hash = null)
    {
        table.Version = version;
        table.ContentHash = hash ?? $"hash-{table.Id}-v{version}";
        return table;
    }

    private static PremiumInvoiceCalculator Calculator(params RateTable[] tables) =>
        new(RateTableCatalog.Tolerant(tables.Length == 0 ? new[] { Versioned(TierTable(), 1) } : tables));

    private static InvoiceCalculation March(PremiumInvoiceCalculator calculator, IEnumerable<RatingEnrollment> enrollments,
        IEnumerable<PremiumInvoice>? history = null) =>
        calculator.Calculate(new InvoiceCalculationRequest
        {
            BillingPeriodStart = D(2026, 3, 1),
            BillingDate = D(2026, 2, 20),
            Enrollments = enrollments.ToList(),
            PriorBilling = BilledLedger.FromInvoices(history ?? Array.Empty<PremiumInvoice>())
        });

    private static PremiumInvoice Issued(DateTime month, InvoiceCalculation calculation, InvoiceStatus status = InvoiceStatus.Sent)
    {
        var invoice = new PremiumInvoice
        {
            InvoiceNumber = $"INV-G1-{month:yyyy-MM}",
            GroupNumber = "G1",
            BillingPeriodStart = month,
            BillingPeriodEnd = month.AddMonths(1).AddDays(-1),
            Status = status,
            PricingSource = PricingSource.RatingEngine
        };
        calculation.ApplyTo(invoice);
        return invoice;
    }

    private static PremiumInvoice Billed(DateTime month, params (string CoverageId, string MemberId, decimal Amount)[] lines)
    {
        var invoice = new PremiumInvoice
        {
            BillingPeriodStart = month,
            Status = InvoiceStatus.Sent,
            PricingSource = PricingSource.RatingEngine,
            LineItems = lines.Select(l => new InvoiceLineItem { CoverageId = l.CoverageId, MemberId = l.MemberId, TotalPremium = l.Amount }).ToList()
        };
        invoice.RecalculateTotals();
        return invoice;
    }

    // ── Rate version on every line ─────────────────────────────────────

    [Fact]
    public void ListBillLine_RecordsTheRateTableVersionAndHowItWasBuilt()
    {
        var calculation = March(Calculator(Versioned(TierTable(), 3, "abc123")),
            new[] { Household("cov-A", Plan, D(2026, 1, 1), null, Subscriber("A")) });

        var line = calculation.LineItems.Should().ContainSingle().Subject;
        line.Should().Match<InvoiceLineItem>(l =>
            l.RateTableId == "rt-PPO-GOLD-tier" && l.RateTableVersion == 3 && l.RateTableHash == "abc123"
            && l.RatingMethod == "Tier" && l.ProrationRule == "Daily" && l.TotalPremium == 500m
            && l.ServicePeriodStart == D(2026, 3, 1) && l.ServicePeriodEnd == D(2026, 3, 31));
        line.RatingSegments.Should().ContainSingle().Which.Should().Match<InvoiceRatingSegment>(s =>
            s.Basis == "full month" && s.Amount == 500m && s.MonthlyPremium == 500m && s.Days == 31 && s.CoverageLevel == "EMP");
    }

    [Fact]
    public void RateChangeMidCycle_BillsEachSideOnItsOwnLine_WithItsOwnVersion()
    {
        // June (30 days): v1 EE 500 through 6/15 = 500 × 15/30 = 250.00; from 6/16 rt-2 v1 EE 600 = 600 × 15/30 = 300.00.
        var first = Versioned(TierTable(), 1);
        first.EffectiveTo = D(2026, 6, 15);
        var second = Versioned(TierTable(), 1);
        second.Id = "rt-2";
        second.EffectiveFrom = D(2026, 6, 16);
        second.TierRates!.EmployeeOnly = 600m;

        var calculation = Calculator(first, second).Calculate(new InvoiceCalculationRequest
        {
            BillingPeriodStart = D(2026, 6, 1),
            BillingDate = D(2026, 5, 20),
            Enrollments = { Household("cov-A", Plan, D(2026, 1, 1), null, Subscriber("A")) }
        });

        calculation.LineItems.Select(l => (l.RateTableId, l.ServicePeriodStart, l.ServicePeriodEnd, l.TotalPremium)).Should().Equal(
            ("rt-PPO-GOLD-tier", (DateTime?)D(2026, 6, 1), (DateTime?)D(2026, 6, 15), 250.00m),
            ("rt-2", (DateTime?)D(2026, 6, 16), (DateTime?)D(2026, 6, 30), 300.00m));
        calculation.Subtotal.Should().Be(550.00m);
    }

    [Fact]
    public void CorrectedRateVersion_ReconcilesInvoicedMonths_AsARateChange()
    {
        // February was billed 500 under v1. v2 of the same table says EE 520 for the whole year:
        // March bills 520 (v2) and February gets +20, recorded against v2.
        var v2 = Versioned(TierTable(), 2);
        v2.TierRates!.EmployeeOnly = 520m;
        var calculation = Calculator(v2).Calculate(new InvoiceCalculationRequest
        {
            BillingPeriodStart = D(2026, 3, 1),
            BillingDate = D(2026, 2, 20),
            MaxRetroMonths = 1,
            Enrollments = { Household("cov-A", Plan, D(2026, 1, 1), null, Subscriber("A")) },
            PriorBilling = BilledLedger.FromInvoices(new[] { Billed(D(2026, 2, 1), ("cov-A", "A", 500m)) })
        });

        calculation.LineItems.Single().Should().Match<InvoiceLineItem>(l => l.TotalPremium == 520m && l.RateTableVersion == 2);
        calculation.Adjustments.Should().ContainSingle().Which.Should().Match<InvoiceAdjustment>(a =>
            a.Type == AdjustmentType.RateChange && a.Amount == 20m && a.ServicePeriodStart == D(2026, 2, 1)
            && a.RateTableVersions!.SequenceEqual(new[] { "rt-PPO-GOLD-tier@v2" }));
    }

    // ── List vs composite ──────────────────────────────────────────────

    private static List<RatingEnrollment> CompositeGroup() => new()
    {
        Household("cov-A", Plan, D(2026, 1, 1), null, Subscriber("A")),
        Household("cov-B", Plan, D(2026, 1, 1), null, Subscriber("B")),
        Household("cov-C", Plan, D(2026, 1, 1), null, Subscriber("C"), Member("C-SP", MemberRelationship.Spouse, D(1990, 1, 1))),
        Household("cov-D", Plan, D(2026, 3, 10), null, Subscriber("D")),
    };

    [Fact]
    public void ListBill_HasALinePerSubscriber()
    {
        var calculation = March(Calculator(), CompositeGroup());

        // A 500, B 500, C 1,000, D 500 × 22/31 = 354.838… → 354.84; total 2,354.84.
        calculation.LineItems.Select(l => (l.MemberId, l.TotalPremium)).Should().Equal(
            ("A", 500m), ("B", 500m), ("C", 1000m), ("D", 354.84m));
        calculation.Subtotal.Should().Be(2354.84m);
    }

    [Fact]
    public void CompositeBill_HasALinePerTier_ProratedLinesApart_SameTotal()
    {
        var list = March(Calculator(), CompositeGroup());
        var lines = CompositeBill.Collapse(list.LineItems);

        lines.Select(l => (l.CoverageLevel, l.Quantity, l.UnitRate, l.TotalPremium, l.CoverageId)).Should().Equal(
            ("EMP", (int?)2, (decimal?)500m, 1000m, (string?)null),
            ("ESP", (int?)1, (decimal?)1000m, 1000m, (string?)null),
            ("EMP", (int?)null, (decimal?)null, 354.84m, (string?)"cov-D"));
        lines[0].Components!.Select(c => (c.CoverageId, c.Amount)).Should().Equal(("cov-A", 500m), ("cov-B", 500m));
        lines[0].RateTableVersion.Should().Be(1);
        lines.Sum(l => l.TotalPremium).Should().Be(list.Subtotal);

        var invoice = new PremiumInvoice { LineItems = lines };
        invoice.RecalculateTotals();
        invoice.MemberCount.Should().Be(4);
        invoice.TotalAmount.Should().Be(2354.84m);
    }

    [Fact]
    public void CompositeInvoice_IsReconciledPerCoverage_ByLaterInvoices()
    {
        var calculator = Calculator();
        var march = March(calculator, CompositeGroup());
        march.LineItems = CompositeBill.Collapse(march.LineItems);
        var marchInvoice = Issued(D(2026, 3, 1), march);

        // B terminated retro on 3/15: April credits B's March 500 − 500 × 15/31 (241.94) = −258.06.
        var april = CompositeGroup();
        april[1].TerminationDate = D(2026, 3, 15);
        var calculation = calculator.Calculate(new InvoiceCalculationRequest
        {
            BillingPeriodStart = D(2026, 4, 1),
            BillingDate = D(2026, 3, 20),
            Enrollments = april,
            PriorBilling = BilledLedger.FromInvoices(new[] { marchInvoice })
        });

        calculation.Adjustments.Should().ContainSingle().Which.Should().Match<InvoiceAdjustment>(a =>
            a.CoverageId == "cov-B" && a.Type == AdjustmentType.RetroTerm && a.Amount == -258.06m);
    }

    // ── Age banding: ages are fixed at the plan-year start ──────────────

    [Fact]
    public void BirthdayMidPeriod_DoesNotChangeTheRate_UntilRenewal()
    {
        // Federal curve, base 300: age 29 → 1.119 (335.70), age 30 → 1.135 (340.50).
        // The subscriber turns 30 on 2026-03-15; ages are measured on the plan-year start (2026-01-01).
        var year2026 = Versioned(AgeBandTable(), 1);
        var year2027 = Versioned(AgeBandTable(), 1);
        year2027.Id = "rt-2027";
        year2027.EffectiveFrom = D(2027, 1, 1);
        year2027.EffectiveTo = D(2027, 12, 31);
        var calculator = Calculator(year2026, year2027);
        var household = Household("cov-1", "HMO-SILVER", D(2026, 1, 1), null, Subscriber("S", D(1996, 3, 15)));

        calculator.ChargeForMonth(household, D(2026, 3, 1))!.Amount.Should().Be(335.70m);
        calculator.ChargeForMonth(household, D(2026, 12, 1))!.Amount.Should().Be(335.70m);
        calculator.ChargeForMonth(household, D(2027, 1, 1))!.Amount.Should().Be(340.50m);
    }

    [Fact]
    public void MemberJoiningMidYear_IsAgedOnTheirOwnStart()
    {
        // Spouse born 1996-06-01 joins 2026-06-20: 30 then (1.135 → 340.50), not 29 on 1/1.
        var spouse = Member("SP", MemberRelationship.Spouse, D(1996, 6, 1));
        spouse.EffectiveDate = D(2026, 6, 20);
        var household = Household("cov-1", "HMO-SILVER", D(2026, 1, 1), null, Subscriber("S", D(1996, 3, 15)), spouse);

        var july = Calculator(Versioned(AgeBandTable(), 1)).ChargeForMonth(household, D(2026, 7, 1))!;

        july.Segments.Single().Rating.Members.Single(m => m.MemberId == "SP").Age.Should().Be(30);
        july.Amount.Should().Be(335.70m + 340.50m);
    }

    [Fact]
    public void MidYearRateRevision_KeepsPlanYearAges_WhenItsAgeDateIsThePlanYearStart()
    {
        // A revision effective 7/1 would re-age everyone on 7/1 unless it names the plan-year age date.
        var first = Versioned(AgeBandTable(), 1);
        first.EffectiveTo = D(2026, 6, 30);
        var revision = Versioned(AgeBandTable(baseRate: 310m), 1);
        revision.Id = "rt-revision";
        revision.EffectiveFrom = D(2026, 7, 1);
        revision.AgeDeterminationDate = D(2026, 1, 1);
        var household = Household("cov-1", "HMO-SILVER", D(2026, 1, 1), null, Subscriber("S", D(1996, 3, 15)));

        // Still 29 (1.119): 310 × 1.119 = 346.89.
        Calculator(first, revision).ChargeForMonth(household, D(2026, 7, 1))!.Amount.Should().Be(346.89m);
    }

    // ── Proration rules ────────────────────────────────────────────────

    private static RateTable HalfMonthTable()
    {
        var table = Versioned(TierTable(), 1);
        table.Proration = ProrationRule.HalfMonth;
        table.TierRates!.EmployeeOnly = 500.01m; // odd cents: halves must not add a cent
        return table;
    }

    [Theory]
    // March: halves 3/1–3/15 and 3/16–3/31. Half of 500.01 = 250.005 → 250.01.
    [InlineData(1, null, 500.01)]   // whole month: exactly the monthly premium, not 2 × 250.01
    [InlineData(10, null, 250.01)]  // starts mid first half: billed from the second half
    [InlineData(16, null, 250.01)]  // starts on the second half
    [InlineData(1, 10, 250.01)]     // ends in the first half: that whole half
    [InlineData(1, 20, 500.01)]     // ends in the second half: both halves
    [InlineData(20, null, null)]    // starts after the second half began: nothing this month
    public void HalfMonthProration(int startDay, int? endDay, double? expected)
    {
        var household = Household("cov-A", Plan, D(2026, 3, startDay), endDay.HasValue ? D(2026, 3, endDay.Value) : null, Subscriber("A"));

        var charge = Calculator(HalfMonthTable()).ChargeForMonth(household, D(2026, 3, 1));

        if (expected == null)
            charge.Should().BeNull();
        else
            charge!.Amount.Should().Be((decimal)expected.Value);
    }

    [Fact]
    public void HalfMonthProration_LineSaysHalfMonth()
    {
        var calculation = March(Calculator(HalfMonthTable()), new[] { Household("cov-A", Plan, D(2026, 3, 10), null, Subscriber("A")) });

        calculation.LineItems.Single().Should().Match<InvoiceLineItem>(l =>
            l.TotalPremium == 250.01m && l.ProrationFactor == 0.5m && l.AdjustmentReason == "Prorated: half month" && l.ProrationRule == "HalfMonth");
    }

    [Theory]
    [InlineData(1, null, 500.0)]  // in force on the 1st
    [InlineData(2, null, null)]   // starts after the 1st: first charged next month
    [InlineData(1, 5, 500.0)]     // ends mid-month: the whole month
    public void FullMonthProration(int startDay, int? endDay, double? expected)
    {
        var table = Versioned(TierTable(), 1);
        table.Proration = ProrationRule.FullMonth;
        var household = Household("cov-A", Plan, D(2026, 3, startDay), endDay.HasValue ? D(2026, 3, endDay.Value) : null, Subscriber("A"));

        var charge = Calculator(table).ChargeForMonth(household, D(2026, 3, 1));

        if (expected == null)
            charge.Should().BeNull();
        else
            charge!.Amount.Should().Be((decimal)expected.Value);
    }

    [Fact]
    public void FullMonthProration_RetroAdd_ChargesOnlyMonthsInForceOnThe1st()
    {
        // Effective 1/2, reported after January and February were invoiced:
        // January is not charged (not in force on 1/1); February +500.
        var table = Versioned(TierTable(), 1);
        table.Proration = ProrationRule.FullMonth;
        var calculation = March(Calculator(table), new[] { Household("cov-N", Plan, D(2026, 1, 2), null, Subscriber("N")) },
            new[] { Billed(D(2026, 1, 1), ("cov-X", "X", 500m)), Billed(D(2026, 2, 1), ("cov-X", "X", 500m)) });

        calculation.Adjustments.Where(a => a.CoverageId == "cov-N").Select(a => (a.ServicePeriodStart, a.Type, a.Amount)).Should().Equal(
            ((DateTime?)D(2026, 2, 1), AdjustmentType.RetroAdd, 500m));
    }

    [Fact]
    public void DailyProration_ChangeThatDoesNotChangeThePremium_IsRoundedOnce()
    {
        // Age band: three children under 21 are rated; a fourth (younger) joining 3/10 is not, so the
        // premium is unchanged and March stays one full-month segment rather than two rounded pieces.
        var child4 = Member("K4", MemberRelationship.Child, D(2024, 1, 1));
        child4.EffectiveDate = D(2026, 3, 10);
        var household = Household("cov-1", "HMO-SILVER", D(2026, 1, 1), null,
            Subscriber("S", D(1980, 6, 15)),
            Member("K1", MemberRelationship.Child, D(2010, 1, 1)),
            Member("K2", MemberRelationship.Child, D(2012, 1, 1)),
            Member("K3", MemberRelationship.Child, D(2014, 1, 1)),
            child4);

        var charge = Calculator(Versioned(AgeBandTable(), 1)).ChargeForMonth(household, D(2026, 3, 1))!;

        charge.Segments.Should().ContainSingle();
        charge.IsProrated.Should().BeFalse();
        charge.Amount.Should().Be(charge.Segments[0].Rating.MonthlyPremium);
    }

    [Fact]
    public void ProrationRule_MayChangeOnlyOnTheFirstOfAMonth()
    {
        var first = Versioned(TierTable(), 1);
        first.EffectiveTo = D(2026, 6, 14);
        var second = Versioned(TierTable(), 1);
        second.Id = "rt-2";
        second.EffectiveFrom = D(2026, 6, 15);
        second.Proration = ProrationRule.FullMonth;

        var act = () => new RateTableCatalog(new[] { first, second });
        act.Should().Throw<RateTableValidationException>().WithMessage("*proration*first day of a month*");

        first.EffectiveTo = D(2026, 6, 30);
        second.EffectiveFrom = D(2026, 7, 1);
        _ = new RateTableCatalog(new[] { first, second });
    }

    // ── Retro adds and terms ───────────────────────────────────────────

    [Fact]
    public void RetroTerm_CreditsWhatWasBilled_OnTheNextInvoice()
    {
        // Family billed 1,400 in February; terminated 2/14 (reported later): due 1,400 × 14/28 = 700, credit −700.
        // Terminated 1/31 instead: nothing due for February, credit −1,400 and no March line.
        var family = Household("cov-B", Plan, D(2026, 1, 1), D(2026, 2, 14),
            Subscriber("B"), Member("B-SP", MemberRelationship.Spouse, D(1986, 1, 1)), Member("B-C", MemberRelationship.Child, D(2015, 1, 1)));
        var history = new[] { Billed(D(2026, 2, 1), ("cov-B", "B", 1400m)) };

        var midMonth = March(Calculator(), new[] { family }, history);
        midMonth.Adjustments.Single().Should().Match<InvoiceAdjustment>(a => a.Type == AdjustmentType.RetroTerm && a.Amount == -700m);

        family.TerminationDate = D(2026, 1, 31);
        var wholeMonth = March(Calculator(), new[] { family }, history);
        wholeMonth.LineItems.Should().BeEmpty();
        wholeMonth.Adjustments.Single().Should().Match<InvoiceAdjustment>(a =>
            a.Type == AdjustmentType.RetroTerm && a.Amount == -1400m && a.RateTableVersions == null);
        wholeMonth.Total.Should().Be(-1400m);
    }

    [Fact]
    public void RetroAdd_ChargesTheMissedMonths_OnTheNextInvoice()
    {
        // Effective 2/1 but reported after February was invoiced: March 500 plus February +500.
        var calculation = March(Calculator(), new[] { Household("cov-N", Plan, D(2026, 2, 1), null, Subscriber("N")) },
            new[] { Billed(D(2026, 2, 1), ("cov-X", "X", 500m)) });

        calculation.LineItems.Where(l => l.CoverageId == "cov-N").Sum(l => l.TotalPremium).Should().Be(500m);
        calculation.Adjustments.Where(a => a.CoverageId == "cov-N").Should().ContainSingle().Which.Should().Match<InvoiceAdjustment>(a =>
            a.Type == AdjustmentType.RetroAdd && a.Amount == 500m && a.IsRatingRetro
            && a.RateTableVersions!.SequenceEqual(new[] { "rt-PPO-GOLD-tier@v1" }));
    }

    [Fact]
    public void DraftInvoices_DoNotCountAsBilled()
    {
        var draft = Billed(D(2026, 2, 1), ("cov-A", "A", 500m));
        draft.Status = InvoiceStatus.Draft;

        var ledger = BilledLedger.FromInvoices(new[] { draft });

        ledger.WasInvoiced(D(2026, 2, 1)).Should().BeFalse();
        ledger.Billed("cov-A", D(2026, 2, 1)).Should().Be(0m);
    }

    [Fact]
    public void UnreconcilableCoverage_IsNotCreditedBack()
    {
        // cov-A was billed in February but its data cannot be rated now: no −500 "retro term".
        var calculation = Calculator().Calculate(new InvoiceCalculationRequest
        {
            BillingPeriodStart = D(2026, 3, 1),
            BillingDate = D(2026, 2, 20),
            PriorBilling = BilledLedger.FromInvoices(new[] { Billed(D(2026, 2, 1), ("cov-A", "A", 500m)) }),
            Unreconcilable = new HashSet<string> { "cov-A" }
        });

        calculation.Adjustments.Should().BeEmpty();
    }

    // ── Missing, expired and invalid rates; bad data ───────────────────

    [Fact]
    public void ExpiredRate_IsAnException_NeverAZeroLine()
    {
        var table = Versioned(TierTable(), 1);
        table.EffectiveTo = D(2026, 2, 28);

        var calculation = March(Calculator(table), new[] { Household("cov-A", Plan, D(2026, 1, 1), null, Subscriber("A")) });

        calculation.LineItems.Should().BeEmpty();
        calculation.Issues.Should().ContainSingle().Which.Should().Match<InvoiceCalculationIssue>(i =>
            i.Code == InvoiceCalculationIssue.RateNotFound && i.CoverageId == "cov-A" && i.Month == D(2026, 3, 1)
            && i.Message.Contains("PPO-GOLD") && i.Message.Contains("2026-03-01"));
    }

    [Fact]
    public void PlanWithNoRateTable_IsAnException_OtherPlansAreStillRated()
    {
        var calculation = March(Calculator(), new[]
        {
            Household("cov-A", Plan, D(2026, 1, 1), null, Subscriber("A")),
            Household("cov-Z", "UNRATED-PLAN", D(2026, 1, 1), null, Subscriber("Z"))
        });

        calculation.LineItems.Should().ContainSingle(l => l.CoverageId == "cov-A");
        calculation.LineItems.Should().NotContain(l => l.TotalPremium == 0m);
        calculation.Issues.Should().ContainSingle(i => i.CoverageId == "cov-Z" && i.Code == InvoiceCalculationIssue.RateNotFound);
    }

    [Fact]
    public void ConflictingRateTables_MakeThePlanAnException_InsteadOfFailingTheInvoice()
    {
        var a = Versioned(TierTable(), 1);
        var b = Versioned(TierTable(), 1);
        b.Id = "rt-overlap";
        b.EffectiveFrom = D(2026, 6, 1);
        var catalog = RateTableCatalog.Tolerant(new[] { a, b, Versioned(AgeBandTable(), 1) });

        catalog.InvalidPlans.Should().ContainKey(Plan);
        var calculation = new PremiumInvoiceCalculator(catalog).Calculate(new InvoiceCalculationRequest
        {
            BillingPeriodStart = D(2026, 3, 1),
            BillingDate = D(2026, 2, 20),
            Enrollments =
            {
                Household("cov-A", Plan, D(2026, 1, 1), null, Subscriber("A")),
                Household("cov-S", "HMO-SILVER", D(2026, 1, 1), null, Subscriber("S", D(1980, 6, 15)))
            }
        });

        calculation.Issues.Should().ContainSingle(i => i.CoverageId == "cov-A" && i.Code == InvoiceCalculationIssue.RateTableInvalid);
        calculation.LineItems.Should().ContainSingle(l => l.CoverageId == "cov-S");
    }

    [Fact]
    public void MemberWithoutDateOfBirth_IsAnEnrollmentDataException()
    {
        var subscriber = Subscriber("A");
        subscriber.DateOfBirth = default;

        var calculation = March(Calculator(Versioned(AgeBandTable(), 1)),
            new[] { Household("cov-A", "HMO-SILVER", D(2026, 1, 1), null, subscriber) });

        calculation.LineItems.Should().BeEmpty();
        calculation.Issues.Should().ContainSingle().Which.Code.Should().Be(InvoiceCalculationIssue.EnrollmentData);
    }

    // ── Rounding ───────────────────────────────────────────────────────

    [Fact]
    public void InvoiceTotal_IsExactlyTheSumOfWholeCentLines()
    {
        // Thirty-one coverages, one added each day of March: every line is a separate rounding of 500 × n/31.
        var enrollments = Enumerable.Range(1, 31)
            .Select(day => Household($"cov-{day:00}", Plan, D(2026, 3, day), null, Subscriber($"M{day:00}")))
            .ToList();

        var calculation = March(Calculator(), enrollments);
        var invoice = Issued(D(2026, 3, 1), calculation);

        calculation.LineItems.Should().HaveCount(31);
        calculation.LineItems.Should().OnlyContain(l => decimal.Round(l.TotalPremium, 2) == l.TotalPremium && l.TotalPremium > 0);
        calculation.LineItems.Should().OnlyContain(l => l.TotalPremium == l.RatingSegments!.Sum(s => s.Amount));
        invoice.TotalAmount.Should().Be(calculation.LineItems.Sum(l => l.TotalPremium));
        // 500 × (31 + 30 + … + 1)/31 = 500 × 16 = 8,000 before rounding; the per-line rounding drifts by
        // cents at most, and the drift is kept, never spread: this exact total is the documented result.
        invoice.TotalAmount.Should().Be(calculation.LineItems.Sum(l => PremiumRatingEngine.RoundMoney(500m * (32 - int.Parse(l.MemberId[1..])) / 31m)));
    }
}
