using CloudHealthOffice.CobEngine.Domain;
using CloudHealthOffice.CobEngine.Services;
using Xunit;

namespace CloudHealthOffice.CobEngine.Tests;

/// <summary>
/// N prior payers (tertiary and later) under NAIC MDL-120 §6.A(4) / §7, and
/// the line- vs claim-level allocation of 837 2320/2430 prior payer data.
/// </summary>
public class TertiaryCobTests
{
    private static readonly CobCalculationService Svc = new();

    private static CobLineInput Unit(CobModel model, params PriorPayerAmount[] prior) => new()
    {
        LineNumber = 1,
        BilledAmount = 200m,
        SecondaryAllowedAmount = 150m,
        SecondaryMemberResponsibilityBeforeCob = 54m,
        SecondaryPlanPaymentBeforeCob = 96m,
        Model = model,
        PriorPayers = prior,
    };

    private static PriorPayerAmount Paid(int sequence, decimal paid, decimal? pr = null) =>
        new() { Sequence = sequence, PaidAmount = paid, PatientResponsibility = pr };

    private static decimal Oa23(CobLineInput i, CobLineResult r) =>
        i.SecondaryAllowedAmount - r.MemberResponsibility - r.SecondaryPlanPayment;

    // allowed 150, normal 96, cost share 54.
    [Theory]
    // Both prior payers paid: 80 + 25 = 105; last PR 15 → balance 15.
    [InlineData(CobModel.Complementary, 80, 25, 15, 15, 0)]
    [InlineData(CobModel.NonDuplication, 80, 25, 15, 0, 15)]
    // Secondary paid 0, PR 40: balance min(70, 40) = 40.
    [InlineData(CobModel.Complementary, 80, 0, 40, 40, 0)]
    [InlineData(CobModel.NonDuplication, 80, 0, 40, 16, 24)]
    // Prior payers paid more than allowed: nothing left.
    [InlineData(CobModel.Complementary, 120, 60, 0, 0, 0)]
    [InlineData(CobModel.NonDuplication, 120, 60, 0, 0, 0)]
    public void Tertiary_TwoPriorPayers(
        CobModel model, decimal primaryPaid, decimal secondaryPaid, decimal secondaryPr,
        decimal expectedPaid, decimal expectedMember)
    {
        var input = Unit(model, Paid(1, primaryPaid, 40m), Paid(2, secondaryPaid, secondaryPr));

        var r = Svc.Calculate(input);

        Assert.Equal(expectedPaid, r.SecondaryPlanPayment);
        Assert.Equal(expectedMember, r.MemberResponsibility);
        Assert.Equal(primaryPaid + secondaryPaid, r.TotalPriorPaid);
        Assert.Equal(primaryPaid, r.PrimaryPayerPayment);
        // Invariants: never more than allowed − prior paid, member never above
        // the cost share, OA-23 never negative.
        Assert.True(r.SecondaryPlanPayment <= Math.Max(0, 150m - r.TotalPriorPaid));
        Assert.True(r.MemberResponsibility <= 54m);
        Assert.True(Oa23(input, r) >= 0);
        Assert.True(r.CobReduction >= 0);
    }

    [Fact]
    public void PriorPayers_AreOrderedBySequence_LastPayerPrIsUsed()
    {
        // Listed out of order: the sequence-2 payer's PR (10) is the last one.
        var r = Svc.Calculate(Unit(CobModel.Complementary, Paid(2, 20m, 10m), Paid(1, 50m, 60m)));

        Assert.Equal(70m, r.TotalPriorPaid);
        Assert.Equal(10m, r.SecondaryPlanPayment); // min(96, min(150 − 70, 10))
        Assert.Equal(0m, r.MemberResponsibility);
    }

    [Fact]
    public void FourthPayer_SumsThreePriorPayers()
    {
        var r = Svc.Calculate(Unit(CobModel.Complementary, Paid(1, 50m), Paid(2, 30m), Paid(3, 20m, 30m)));

        Assert.Equal(100m, r.TotalPriorPaid);
        Assert.Equal(30m, r.SecondaryPlanPayment); // min(96, min(50, 30))
        Assert.Equal(0m, r.MemberResponsibility);
    }

    [Fact]
    public void PriorPayersEmpty_FallsBackToLegacyPrimaryPayment()
    {
        var r = Svc.Calculate(Unit(CobModel.Complementary) with { PrimaryPayerPayment = 40m });

        Assert.Equal(40m, r.TotalPriorPaid);
        Assert.Equal(96m, r.SecondaryPlanPayment);
        Assert.Equal(14m, r.MemberResponsibility);
    }

    [Fact]
    public void Complementary_NeverPaysMoreThanAllowedLessPriorPaid_EvenWhenBilledIsHigher()
    {
        // Billed 200, allowed 150, prior paid 120: at most 30 (not 200 − 120).
        var r = Svc.Calculate(Unit(CobModel.Complementary, Paid(1, 120m)));

        Assert.Equal(30m, r.SecondaryPlanPayment);
        Assert.Equal(0m, r.MemberResponsibility);
    }

    // ── PayerResponsibility (SBR01) ───────────────────────────────────────

    [Theory]
    [InlineData("P", 1)]
    [InlineData("S", 2)]
    [InlineData("T", 3)]
    [InlineData("A", 4)]
    [InlineData("H", 11)]
    [InlineData("t", 3)]
    [InlineData("U", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void PayerResponsibility_MapsSbr01(string? sbr01, int? expected) =>
        Assert.Equal(expected, PayerResponsibility.ToSequence(sbr01));

    // ── PriorPayerAllocator ───────────────────────────────────────────────

    private static readonly PriorPayerAllocator.ClaimLineCharge[] TwoLines =
    [
        new(1, 200m),
        new(2, 100m),
    ];

    private static PriorPayerAdjustment Adj(string group, string carc, decimal amount) =>
        new() { GroupCode = group, ReasonCode = carc, Amount = amount };

    [Fact]
    public void Allocator_ClaimLevelOnly_ProratesPaidAndPrByCharge()
    {
        var payer = new PriorPayerAdjudication
        {
            Sequence = 1,
            ClaimPaidAmount = 100m,
            ClaimAdjustments = [Adj("PR", "1", 50m), Adj("CO", "45", 150m)],
        };

        var byLine = PriorPayerAllocator.AllocateToLines(TwoLines, [payer], ourSequence: 2);

        Assert.Equal(66.66m, byLine[1][0].PaidAmount);
        Assert.Equal(33.34m, byLine[2][0].PaidAmount);
        Assert.Equal(33.33m, byLine[1][0].PatientResponsibility);
        Assert.Equal(16.67m, byLine[2][0].PatientResponsibility);
    }

    [Fact]
    public void Allocator_LineLevelPreferred_RemainderToUnreportedLines()
    {
        var payer = new PriorPayerAdjudication
        {
            Sequence = 1,
            ClaimPaidAmount = 120m,
            ClaimAdjustments = [Adj("PR", "2", 10m)],
            Lines = [new() { LineNumber = 1, PaidAmount = 80m, Adjustments = [Adj("PR", "1", 40m)] }],
        };

        var byLine = PriorPayerAllocator.AllocateToLines(TwoLines, [payer], ourSequence: 3);

        Assert.Equal(80m, byLine[1][0].PaidAmount);
        Assert.Equal(40m, byLine[1][0].PatientResponsibility);
        Assert.Equal(40m, byLine[2][0].PaidAmount);
        Assert.Equal(10m, byLine[2][0].PatientResponsibility);
    }

    [Fact]
    public void Allocator_AllLinesReported_ClaimLevelAdjustmentReducesLinePayments()
    {
        // TR3: AMT*D = Σ SVD02 − Σ 2320 CAS → 90 = (60 + 40) − 10.
        var payer = new PriorPayerAdjudication
        {
            Sequence = 1,
            ClaimPaidAmount = 90m,
            ClaimAdjustments = [Adj("PR", "1", 10m)],
            Lines =
            [
                new() { LineNumber = 1, PaidAmount = 60m, Adjustments = [Adj("CO", "45", 140m)] },
                new() { LineNumber = 2, PaidAmount = 40m, Adjustments = [Adj("CO", "45", 60m)] },
            ],
        };

        var byLine = PriorPayerAllocator.AllocateToLines(TwoLines, [payer], ourSequence: 2);

        Assert.Equal(54m, byLine[1][0].PaidAmount);
        Assert.Equal(36m, byLine[2][0].PaidAmount);
        Assert.Equal(90m, byLine.Values.Sum(v => v[0].PaidAmount));
        // Claim-level PR prorated by charge across all lines.
        Assert.Equal(6.66m, byLine[1][0].PatientResponsibility);
        Assert.Equal(3.34m, byLine[2][0].PatientResponsibility);
    }

    [Fact]
    public void Allocator_NoCas_PatientResponsibilityUnknown()
    {
        var payer = new PriorPayerAdjudication { Sequence = 1, ClaimPaidAmount = 30m };

        var byLine = PriorPayerAllocator.AllocateToLines(TwoLines, [payer], ourSequence: 2);

        Assert.Null(byLine[1][0].PatientResponsibility);
        Assert.Null(byLine[2][0].PatientResponsibility);
    }

    [Fact]
    public void Allocator_IgnoresPayersAtOrAfterOurSequence_AndOrdersBySequence()
    {
        var payers = new[]
        {
            new PriorPayerAdjudication { Sequence = 2, ClaimPaidAmount = 20m },
            new PriorPayerAdjudication { Sequence = 3, ClaimPaidAmount = 999m },
            new PriorPayerAdjudication { Sequence = 1, ClaimPaidAmount = 50m },
        };

        var claim = PriorPayerAllocator.AllocateToClaim(TwoLines, payers, ourSequence: 3);

        Assert.Equal(new[] { 1, 2 }, claim.Select(p => p.Sequence));
        Assert.Equal(new[] { 50m, 20m }, claim.Select(p => p.PaidAmount));
    }

    [Fact]
    public void Allocator_ClaimTotals_EqualAmtD_AndSumOfLinePr()
    {
        var payer = new PriorPayerAdjudication
        {
            Sequence = 2,
            ClaimPaidAmount = 3000m,
            Lines =
            [
                new() { LineNumber = 1, PaidAmount = 1000m, Adjustments = [Adj("PR", "2", 300m)] },
                new() { LineNumber = 2, PaidAmount = 2000m, Adjustments = [Adj("PR", "2", 700m)] },
            ],
        };

        var claim = PriorPayerAllocator.AllocateToClaim(TwoLines, [payer], ourSequence: 3);

        Assert.Equal(3000m, Assert.Single(claim).PaidAmount);
        Assert.Equal(1000m, claim[0].PatientResponsibility);
    }
}
