using CloudHealthOffice.CobEngine.Domain;
using CloudHealthOffice.CobEngine.Services;
using Xunit;

namespace CloudHealthOffice.CobEngine.Tests;

/// <summary>
/// <see cref="CobCalculationService.CalculateClaim"/>: NAIC MDL-120 §7 limits
/// applied "for that claim", a prior payer's denial not bounding later
/// payers, and the distribution to lines.
/// </summary>
public class ClaimLevelCobTests
{
    private static readonly CobCalculationService Svc = new();

    private static PriorPayerAdjustment Adj(string group, string carc, decimal amount) =>
        new() { GroupCode = group, ReasonCode = carc, Amount = amount };

    /// <summary>
    /// Golden 07. Tertiary; L1 billed 400 / allowed 150 / cost share 78;
    /// L2 billed 180 / allowed 100 / cost share 20.
    /// Primary 2430: L1 paid 40 PR-1 100, L2 paid 72 PR-2 18.
    /// Secondary 2320 only: AMT*D 60, PR 58 (OA-23 462 is not PR).
    /// Prior paid: L1 40 + 41.37 = 81.37, L2 72 + 18.63 = 90.63 (the 60
    /// prorated by charge 400 : 180). Room 68.63 + 9.37 = 78. The secondary
    /// adjudicated both lines: balance = min(78, 58) = 58. Pay min(152, 58)
    /// = 58 (line-level capping with the PR prorated by charge paid 49.37).
    /// Line balances by room: 58 × 68.63 / 78 = 51.03, 6.97 — and the
    /// payment fills them: L1 51.03, L2 6.97. Member 0.
    /// </summary>
    [Fact]
    public void Golden07_ClaimLevelBalance_PaysTheSecondarysWholePr()
    {
        var result = Svc.CalculateClaim(new CobClaimInput
        {
            OurSequence = 3,
            Units =
            [
                new() { LineNumber = 1, BilledAmount = 400m, AllowedAmount = 150m, CostShareBeforeCob = 78m },
                new() { LineNumber = 2, BilledAmount = 180m, AllowedAmount = 100m, CostShareBeforeCob = 20m },
            ],
            PriorPayers =
            [
                new()
                {
                    Sequence = 1, ClaimPaidAmount = 112m,
                    Lines =
                    [
                        new() { LineNumber = 1, PaidAmount = 40m, Adjustments = [Adj("CO", "45", 260m), Adj("PR", "1", 100m)] },
                        new() { LineNumber = 2, PaidAmount = 72m, Adjustments = [Adj("CO", "45", 90m), Adj("PR", "2", 18m)] },
                    ],
                },
                new()
                {
                    Sequence = 2, ClaimPaidAmount = 60m,
                    ClaimAdjustments = [Adj("OA", "23", 462m), Adj("PR", "1", 50m), Adj("PR", "2", 8m)],
                },
            ],
        });

        Assert.Equal(58m, result.Balance);
        Assert.Equal(58m, result.PlanPayment);
        Assert.Equal(0m, result.MemberResponsibility);
        Assert.Equal(172m, result.TotalPriorPaid);
        Assert.Equal(new[] { 51.03m, 6.97m }, result.Units.Select(u => u.SecondaryPlanPayment));
        Assert.Equal(new[] { 81.37m, 90.63m }, result.Units.Select(u => u.TotalPriorPaid));
    }

    /// <summary>B1 at claim level: a primary that paid $0 with only CO (no PR) does not bound.</summary>
    [Theory]
    [InlineData("27")]
    [InlineData("22")]
    [InlineData("109")]
    [InlineData("96")]
    [InlineData("204")]
    public void Denial_IsUnbounded(string carc)
    {
        var result = Svc.CalculateClaim(new CobClaimInput
        {
            Units = [new() { LineNumber = 1, BilledAmount = 500m, AllowedAmount = 300m, CostShareBeforeCob = 60m }],
            PriorPayers = [new() { Sequence = 1, ClaimPaidAmount = 0m, ClaimAdjustments = [Adj("CO", carc, 500m)] }],
        });

        Assert.Equal(300m, result.Balance);
        Assert.Equal(240m, result.PlanPayment);
        Assert.Equal(60m, result.MemberResponsibility);
    }

    /// <summary>
    /// Re-review N2. The prior payer reported both lines in 2430 with $0 paid
    /// and no line CAS, but its 2320 has CAS*PR-1 $120 (the deductible) —
    /// it adjudicated, the member owes $120 after it. Bound = $120, so we pay
    /// $120, not the $200 an "unadjudicated" reading allowed (an $80
    /// overpayment). The single-stay path (claim totals) agrees.
    /// </summary>
    [Fact]
    public void ZeroPaid2430Lines_WithClaimLevelPr_AreAdjudicated()
    {
        PriorPayerAdjudication Payer() => new()
        {
            Sequence = 1,
            ClaimPaidAmount = 0m,
            ClaimAdjustments = [Adj("PR", "1", 120m), Adj("CO", "45", 80m)],
            Lines =
            [
                new() { LineNumber = 1, PaidAmount = 0m },
                new() { LineNumber = 2, PaidAmount = 0m },
            ],
        };

        var lines = Svc.CalculateClaim(new CobClaimInput
        {
            Units =
            [
                new() { LineNumber = 1, BilledAmount = 100m, AllowedAmount = 100m, CostShareBeforeCob = 0m },
                new() { LineNumber = 2, BilledAmount = 100m, AllowedAmount = 100m, CostShareBeforeCob = 0m },
            ],
            PriorPayers = [Payer()],
        });
        var stay = Svc.CalculateClaim(new CobClaimInput
        {
            SingleStay = true,
            Units = [new() { LineNumber = 0, BilledAmount = 200m, AllowedAmount = 200m, CostShareBeforeCob = 0m }],
            PriorPayers = [Payer()],
        });

        Assert.Equal(120m, lines.PlanPayment);
        Assert.Equal(120m, stay.PlanPayment);
    }

    [Fact]
    public void PerLineCalculate_DenialIsUnbounded_AdjudicatedPrBounds()
    {
        var input = new CobLineInput
        {
            LineNumber = 1, BilledAmount = 500m, SecondaryAllowedAmount = 300m,
            SecondaryMemberResponsibilityBeforeCob = 60m, SecondaryPlanPaymentBeforeCob = 240m,
            Model = CobModel.Complementary,
        };

        var denied = Svc.Calculate(input with { PriorPayers = [new() { Sequence = 1, PaidAmount = 0m, PatientResponsibility = 0m }] });
        Assert.Equal(240m, denied.SecondaryPlanPayment);

        var tertiary = Svc.Calculate(input with
        {
            PriorPayers =
            [
                new() { Sequence = 1, PaidAmount = 100m, PatientResponsibility = 50m },
                new() { Sequence = 2, PaidAmount = 0m, PatientResponsibility = 0m },
            ],
        });
        Assert.Equal(50m, tertiary.SecondaryPlanPayment);
    }

    /// <summary>
    /// Every line paid stays within its allowed − prior paid, and member +
    /// paid never exceeds allowed (OA-23 ≥ 0), whatever the mix.
    /// </summary>
    [Fact]
    public void Distribution_RespectsLineLimits()
    {
        var result = Svc.CalculateClaim(new CobClaimInput
        {
            Units =
            [
                new() { LineNumber = 1, BilledAmount = 400m, AllowedAmount = 300m, CostShareBeforeCob = 300m },
                new() { LineNumber = 2, BilledAmount = 200m, AllowedAmount = 100m, CostShareBeforeCob = 44m },
                new() { LineNumber = 3, BilledAmount = 0m, AllowedAmount = 0m, CostShareBeforeCob = 0m },
            ],
            PriorPayers = [new() { Sequence = 1, ClaimPaidAmount = 150m }],
        });

        Assert.Equal(56m, result.PlanPayment);
        Assert.Equal(194m, result.MemberResponsibility);
        var units = result.Units;
        var allowed = new[] { 300m, 100m, 0m };
        for (var i = 0; i < 3; i++)
        {
            Assert.True(units[i].SecondaryPlanPayment <= Math.Max(0, allowed[i] - units[i].TotalPriorPaid));
            Assert.True(units[i].SecondaryPlanPayment + units[i].MemberResponsibility <= allowed[i]);
        }
        Assert.Equal(new[] { 6m, 50m, 0m }, units.Select(u => u.SecondaryPlanPayment));
        Assert.Equal(new[] { 100m, 50m, 0m }, units.Select(u => u.TotalPriorPaid));
    }

    [Fact]
    public void NonDuplication_ClaimLevel()
    {
        // normal 240 + 240, prior paid 300 → pay min(480 − 300, balance 300) = 180.
        var result = Svc.CalculateClaim(new CobClaimInput
        {
            Model = CobModel.NonDuplication,
            Units =
            [
                new() { LineNumber = 1, BilledAmount = 300m, AllowedAmount = 300m, CostShareBeforeCob = 60m },
                new() { LineNumber = 2, BilledAmount = 300m, AllowedAmount = 300m, CostShareBeforeCob = 60m },
            ],
            PriorPayers = [new() { Sequence = 1, ClaimPaidAmount = 300m }],
        });

        Assert.Equal(180m, result.PlanPayment);
        Assert.Equal(120m, result.MemberResponsibility);
    }

    [Fact]
    public void Prorate_RemainderOnLastPositiveWeight_NeverOnAZeroChargeLine()
    {
        Assert.Equal(new[] { 5.00m, 5.01m, 0m }, PriorPayerAllocator.Prorate(10.01m, [1m, 1m, 0m]));
        Assert.Equal(new[] { 0m, 10.01m, 0m }, PriorPayerAllocator.Prorate(10.01m, [0m, 3m, 0m]));
        // Every weight zero: equal shares, remainder on the last entry.
        Assert.Equal(new[] { 3.33m, 3.33m, 3.34m }, PriorPayerAllocator.Prorate(10m, [0m, 0m, 0m]));
    }

    [Fact]
    public void Allocator_ClaimResidual_NotOnAZeroChargeLine()
    {
        var byLine = PriorPayerAllocator.AllocateToLines(
            [new(1, 100m), new(2, 100m), new(3, 0m)],
            [new PriorPayerAdjudication { Sequence = 1, ClaimPaidAmount = 10.01m }],
            ourSequence: 2);

        Assert.Equal(0m, byLine[3][0].PaidAmount);
        Assert.Equal(10.01m, byLine[1][0].PaidAmount + byLine[2][0].PaidAmount);
    }
}
