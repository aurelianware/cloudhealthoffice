using PremiumBillingService.Models;
using PremiumBillingService.Rating;
using static PremiumBillingService.Tests.Rating.RatingFixtures;

namespace PremiumBillingService.Tests.Rating;

public class PremiumInvoiceCalculatorTests
{
    private const string Plan = "PPO-GOLD"; // tier: EE 500, ES 1000, EC 900, FAM 1400

    private static PremiumInvoiceCalculator Calculator(params RateTable[] tables) =>
        new(new RateTableCatalog(tables.Length == 0 ? new[] { TierTable() } : tables));

    private static PremiumInvoice PriorInvoice(DateTime month, params (string CoverageId, string MemberId, decimal Amount)[] lines)
    {
        var invoice = new PremiumInvoice
        {
            InvoiceNumber = $"INV-G1-{month:yyyy-MM}",
            GroupNumber = "G1",
            BillingPeriodStart = month,
            BillingPeriodEnd = month.AddMonths(1).AddDays(-1),
            Status = InvoiceStatus.Paid,
            LineItems = lines.Select(l => new InvoiceLineItem { CoverageId = l.CoverageId, MemberId = l.MemberId, TotalPremium = l.Amount }).ToList()
        };
        invoice.RecalculateTotals();
        return invoice;
    }

    private static List<RatingEnrollment> MarchCoverage() => new()
    {
        // A: EE since January, unchanged.
        Household("cov-A", Plan, D(2026, 1, 1), null, Subscriber("A")),
        // B: family since January, terminated 2026-02-14 after February was billed in full (retro term).
        Household("cov-B", Plan, D(2026, 1, 1), D(2026, 2, 14),
            Subscriber("B"), Member("B-SP", MemberRelationship.Spouse, D(1986, 1, 1)), Member("B-C", MemberRelationship.Child, D(2015, 1, 1))),
        // C: EE+spouse effective 2026-01-16, reported after January and February were billed (retro add).
        Household("cov-C", Plan, D(2026, 1, 16), null, Subscriber("C"), Member("C-SP", MemberRelationship.Spouse, D(1990, 1, 1))),
        // D: EE effective 2026-03-10 (mid-month add in the billed month).
        Household("cov-D", Plan, D(2026, 3, 10), null, Subscriber("D")),
    };

    private static List<PremiumInvoice> JanuaryAndFebruary() => new()
    {
        PriorInvoice(D(2026, 1, 1), ("cov-A", "A", 500m), ("cov-B", "B", 1400m)),
        PriorInvoice(D(2026, 2, 1), ("cov-A", "A", 500m), ("cov-B", "B", 1400m)),
    };

    [Fact]
    public void MarchInvoice_HandChecked_ProrationRetroAddsAndRetroTerm()
    {
        var calculation = Calculator().Calculate(new InvoiceCalculationRequest
        {
            BillingPeriodStart = D(2026, 3, 1),
            BillingDate = D(2026, 2, 20),
            Enrollments = MarchCoverage(),
            PriorBilling = BilledLedger.FromInvoices(JanuaryAndFebruary())
        });

        // Current month (March, 31 days):
        //   cov-A EE full month                       500.00
        //   cov-B terminated in February                  —
        //   cov-C EE+Spouse full month               1,000.00
        //   cov-D EE from 3/10: 500 × 22/31 = 354.838… 354.84
        //   Subtotal                                 1,854.84
        calculation.LineItems.Select(l => (l.CoverageId, l.CoverageLevel, l.TotalPremium)).Should().Equal(
            ("cov-A", "EMP", 500.00m),
            ("cov-C", "ESP", 1000.00m),
            ("cov-D", "EMP", 354.84m));
        var d = calculation.LineItems.Single(l => l.CoverageId == "cov-D");
        d.ProrationFactor.Should().Be(0.7097m);
        d.AdjustmentReason.Should().Be("Prorated: 22/31 days");
        calculation.Subtotal.Should().Be(1854.84m);

        // Retro (January and February were invoiced; December was not, so it is not reconciled):
        //   2026-01 cov-C due 1,000 × 16/31 = 516.129… → 516.13, billed 0       +516.13 RetroAdd
        //   2026-02 cov-B due 1,400 × 14/28 = 700.00,  billed 1,400.00          −700.00 RetroTerm
        //   2026-02 cov-C due 1,000.00 (full month),   billed 0              +1,000.00 RetroAdd
        //   Adjustments                                                         +816.13
        calculation.Adjustments.Select(a => (a.ServicePeriodStart, a.CoverageId, a.Type, a.Amount)).Should().Equal(
            (D(2026, 1, 1), "cov-C", AdjustmentType.RetroAdd, 516.13m),
            (D(2026, 2, 1), "cov-B", AdjustmentType.RetroTerm, -700.00m),
            (D(2026, 2, 1), "cov-C", AdjustmentType.RetroAdd, 1000.00m));
        calculation.Adjustments[1].Description.Should().Be("2026-02 B (cov-B): due 700.00 (14/28 days), billed 1400.00");
        calculation.TotalAdjustments.Should().Be(816.13m);

        // Invoice total 1,854.84 + 816.13 = 2,670.97
        calculation.Total.Should().Be(2670.97m);

        var invoice = new PremiumInvoice { BillingPeriodStart = D(2026, 3, 1) };
        calculation.ApplyTo(invoice);
        invoice.TotalAmount.Should().Be(2670.97m);
        invoice.BalanceDue.Should().Be(2670.97m);
        invoice.MemberCount.Should().Be(3);
    }

    [Fact]
    public void AprilInvoice_DoesNotRepeatAdjustmentsAlreadyBilledInMarch()
    {
        var calculator = Calculator();
        var history = JanuaryAndFebruary();
        var march = new PremiumInvoice { InvoiceNumber = "INV-G1-2026-03", BillingPeriodStart = D(2026, 3, 1), Status = InvoiceStatus.Sent };
        calculator.Calculate(new InvoiceCalculationRequest
        {
            BillingPeriodStart = D(2026, 3, 1),
            BillingDate = D(2026, 2, 20),
            Enrollments = MarchCoverage(),
            PriorBilling = BilledLedger.FromInvoices(history)
        }).ApplyTo(march);
        history.Add(march);

        var april = calculator.Calculate(new InvoiceCalculationRequest
        {
            BillingPeriodStart = D(2026, 4, 1),
            BillingDate = D(2026, 3, 20),
            Enrollments = MarchCoverage(),
            PriorBilling = BilledLedger.FromInvoices(history)
        });

        april.Adjustments.Should().BeEmpty();
        april.Subtotal.Should().Be(2000m); // A 500 + C 1,000 + D 500
    }

    [Fact]
    public void VoidedInvoices_CountAsNotBilled()
    {
        var invoices = JanuaryAndFebruary();
        invoices[1].Status = InvoiceStatus.Voided;

        var ledger = BilledLedger.FromInvoices(invoices);

        ledger.WasInvoiced(D(2026, 2, 1)).Should().BeFalse();
        ledger.Billed("cov-A", D(2026, 2, 1)).Should().Be(0m);
        ledger.Billed("cov-A", D(2026, 1, 1)).Should().Be(500m);
    }

    [Fact]
    public void TierChange_InAnInvoicedMonth_IsARateChangeAdjustment()
    {
        // cov-A was billed EE (500) for February but was family all along.
        var enrollments = new List<RatingEnrollment>
        {
            Household("cov-A", Plan, D(2026, 1, 1), null, Subscriber("A"),
                Member("A-SP", MemberRelationship.Spouse, D(1986, 1, 1)), Member("A-C", MemberRelationship.Child, D(2015, 1, 1)))
        };

        var calculation = Calculator().Calculate(new InvoiceCalculationRequest
        {
            BillingPeriodStart = D(2026, 3, 1),
            BillingDate = D(2026, 2, 20),
            MaxRetroMonths = 1,
            Enrollments = enrollments,
            PriorBilling = BilledLedger.FromInvoices(new[] { PriorInvoice(D(2026, 2, 1), ("cov-A", "A", 500m)) })
        });

        calculation.Adjustments.Should().ContainSingle()
            .Which.Should().Match<InvoiceAdjustment>(a => a.Type == AdjustmentType.RateChange && a.Amount == 900m);
        calculation.LineItems.Single().TotalPremium.Should().Be(1400m);
    }

    [Fact]
    public void RetroBeyondTheLookback_IsNotReconciled()
    {
        var calculation = Calculator().Calculate(new InvoiceCalculationRequest
        {
            BillingPeriodStart = D(2026, 3, 1),
            BillingDate = D(2026, 2, 20),
            MaxRetroMonths = 1,
            Enrollments = MarchCoverage(),
            PriorBilling = BilledLedger.FromInvoices(JanuaryAndFebruary())
        });

        calculation.Adjustments.Should().OnlyContain(a => a.ServicePeriodStart == D(2026, 2, 1));
    }

    [Fact]
    public void MidMonthRateChange_IsBilledOnBothSides()
    {
        var first = TierTable();
        first.EffectiveTo = D(2026, 6, 15);
        var second = TierTable();
        second.Id = "rt-2";
        second.EffectiveFrom = D(2026, 6, 16);
        second.TierRates!.EmployeeOnly = 600m;

        var charge = Calculator(first, second).ChargeForMonth(Household("cov-A", Plan, D(2026, 1, 1), null, Subscriber("A")), D(2026, 6, 1))!;

        // June has 30 days: 500 × 15/30 = 250.00 plus 600 × 15/30 = 300.00
        charge.Segments.Select(s => s.Amount).Should().Equal(250.00m, 300.00m);
        charge.Amount.Should().Be(550.00m);
    }

    [Fact]
    public void EmployerContribution_SplitsTheLine()
    {
        var calculation = Calculator().Calculate(new InvoiceCalculationRequest
        {
            BillingPeriodStart = D(2026, 3, 1),
            BillingDate = D(2026, 2, 20),
            EmployerContributionPercent = 75m,
            Enrollments = new List<RatingEnrollment> { Household("cov-D", Plan, D(2026, 3, 10), null, Subscriber("D")) }
        });

        var line = calculation.LineItems.Single();
        line.TotalPremium.Should().Be(354.84m);
        line.EmployerContribution.Should().Be(266.13m);
        line.SubscriberPremium.Should().Be(88.71m);
    }

    [Fact]
    public void AgeBandPlan_InvoiceLineIsTheHouseholdPremium()
    {
        var calculator = new PremiumInvoiceCalculator(new RateTableCatalog(new[] { AgeBandTable() }));
        var calculation = calculator.Calculate(new InvoiceCalculationRequest
        {
            BillingPeriodStart = D(2026, 1, 1),
            BillingDate = D(2025, 12, 20),
            Enrollments = new List<RatingEnrollment>
            {
                Household("cov-1", "HMO-SILVER", D(2026, 1, 1), null,
                    Subscriber("S", D(1980, 6, 15)), Member("SP", MemberRelationship.Spouse, D(1982, 3, 1)))
            }
        });

        // 300 × 1.444 = 433.20 + 300 × 1.357 = 407.10
        calculation.LineItems.Single().TotalPremium.Should().Be(840.30m);
    }
}
