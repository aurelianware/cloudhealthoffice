using CloudHealthOffice.CobEngine.Domain;
using CloudHealthOffice.CobEngine.Services;
using Xunit;

namespace CloudHealthOffice.CobEngine.Tests;

public class CobCalculationServiceTests
{
    private static CobCalculationService Make() => new();

    // ── Complementary model ───────────────────────────────────────────────

    [Fact]
    public void Complementary_PrimaryPaidPartial_SecondaryFillsGap()
    {
        // Billed $500, primary paid $300, secondary allowed $500,
        // secondary waterfall produces $150 member resp / $350 plan pay
        var result = Make().Calculate(new CobLineInput
        {
            LineNumber = 1,
            BilledAmount = 500m,
            SecondaryAllowedAmount = 500m,
            SecondaryMemberResponsibilityBeforeCob = 150m,
            SecondaryPlanPaymentBeforeCob = 350m,
            PrimaryPayerPayment = 300m,
            Model = CobModel.Complementary
        });

        // effectiveBalance = 500 - 300 = 200
        // secondaryPay = min(350, 200) = 200
        // memberResp = max(0, 500 - 300 - 200) = 0
        Assert.Equal(200m, result.SecondaryPlanPayment);
        Assert.Equal(0m, result.MemberResponsibility);
        Assert.Equal(150m, result.CobReduction); // 350 - 200
        Assert.True(result.CobApplied);
    }

    [Fact]
    public void Complementary_PrimaryPaidInFull_SecondaryPaysNothing()
    {
        var result = Make().Calculate(new CobLineInput
        {
            LineNumber = 1,
            BilledAmount = 500m,
            SecondaryAllowedAmount = 500m,
            SecondaryMemberResponsibilityBeforeCob = 100m,
            SecondaryPlanPaymentBeforeCob = 400m,
            PrimaryPayerPayment = 500m, // Primary paid billed amount in full
            Model = CobModel.Complementary
        });

        Assert.Equal(0m, result.SecondaryPlanPayment);
        Assert.Equal(0m, result.MemberResponsibility);
        Assert.Equal(400m, result.CobReduction);
        Assert.True(result.CobApplied);
    }

    [Fact]
    public void Complementary_PrimaryPaidMore_NoNegativeMemberResp()
    {
        // Rare edge: primary paid more than billed (e.g. global fee on multi-claim)
        var result = Make().Calculate(new CobLineInput
        {
            LineNumber = 1,
            BilledAmount = 300m,
            SecondaryAllowedAmount = 300m,
            SecondaryMemberResponsibilityBeforeCob = 50m,
            SecondaryPlanPaymentBeforeCob = 250m,
            PrimaryPayerPayment = 350m, // More than billed
            Model = CobModel.Complementary
        });

        Assert.Equal(0m, result.SecondaryPlanPayment);
        Assert.Equal(0m, result.MemberResponsibility); // Never negative
    }

    [Fact]
    public void Complementary_PrimaryPaidZero_SecondaryPaysFullWaterfall()
    {
        var result = Make().Calculate(new CobLineInput
        {
            LineNumber = 1,
            BilledAmount = 500m,
            SecondaryAllowedAmount = 500m,
            SecondaryMemberResponsibilityBeforeCob = 100m,
            SecondaryPlanPaymentBeforeCob = 400m,
            PrimaryPayerPayment = 0m,
            Model = CobModel.Complementary
        });

        // effectiveBalance = 500, secondaryPay = min(400, 500) = 400 — unchanged
        Assert.Equal(400m, result.SecondaryPlanPayment);
        Assert.Equal(100m, result.MemberResponsibility);
        Assert.Equal(0m, result.CobReduction);
        Assert.False(result.CobApplied);
    }

    [Fact]
    public void Complementary_CobReduction_IsNonNegative()
    {
        var result = Make().Calculate(new CobLineInput
        {
            LineNumber = 1,
            BilledAmount = 200m,
            SecondaryAllowedAmount = 200m,
            SecondaryMemberResponsibilityBeforeCob = 50m,
            SecondaryPlanPaymentBeforeCob = 150m,
            PrimaryPayerPayment = 50m,
            Model = CobModel.Complementary
        });

        // effectiveBalance = 150, secondaryPay = min(150, 150) = 150 — no reduction
        Assert.Equal(150m, result.SecondaryPlanPayment);
        Assert.Equal(0m, result.CobReduction);
    }

    // ── Non-duplication model ─────────────────────────────────────────────

    [Fact]
    public void NonDuplication_PrimaryPayedLessThanSecondaryBenefit_SecondaryTopsUp()
    {
        // Secondary: allowed $500, member resp $100, so max benefit = $400
        // Primary paid $250 — secondary tops up to $400: pays $150
        var result = Make().Calculate(new CobLineInput
        {
            LineNumber = 1,
            BilledAmount = 600m,
            SecondaryAllowedAmount = 500m,
            SecondaryMemberResponsibilityBeforeCob = 100m,
            SecondaryPlanPaymentBeforeCob = 400m,
            PrimaryPayerPayment = 250m,
            Model = CobModel.NonDuplication
        });

        // maxBenefit = 500 - 100 = 400; secondary pays 400 - 250 = 150
        Assert.Equal(150m, result.SecondaryPlanPayment);
        Assert.Equal(200m, result.MemberResponsibility); // 600 - 250 - 150
        Assert.Equal(250m, result.CobReduction); // 400 - 150
        Assert.True(result.CobApplied);
    }

    [Fact]
    public void NonDuplication_PrimaryPaidEqualToSecondaryBenefit_SecondaryPaysNothing()
    {
        var result = Make().Calculate(new CobLineInput
        {
            LineNumber = 1,
            BilledAmount = 500m,
            SecondaryAllowedAmount = 500m,
            SecondaryMemberResponsibilityBeforeCob = 100m,
            SecondaryPlanPaymentBeforeCob = 400m,
            PrimaryPayerPayment = 400m, // Exactly equals max secondary benefit
            Model = CobModel.NonDuplication
        });

        Assert.Equal(0m, result.SecondaryPlanPayment);
        Assert.Equal(100m, result.MemberResponsibility); // 500 - 400 - 0
    }

    [Fact]
    public void NonDuplication_PrimaryExceedsSecondaryBenefit_SecondaryPaysNothing()
    {
        var result = Make().Calculate(new CobLineInput
        {
            LineNumber = 1,
            BilledAmount = 500m,
            SecondaryAllowedAmount = 400m,
            SecondaryMemberResponsibilityBeforeCob = 80m,
            SecondaryPlanPaymentBeforeCob = 320m,
            PrimaryPayerPayment = 450m, // Exceeds secondary max benefit of 320
            Model = CobModel.NonDuplication
        });

        // memberResp = max(0, 500 - 450 - 0) = 50
        Assert.Equal(0m, result.SecondaryPlanPayment);
        Assert.Equal(50m, result.MemberResponsibility);
    }

    [Fact]
    public void NonDuplication_PrimaryExceedsSecondaryBenefit_MemberRespIsRemainingBalance()
    {
        var result = Make().Calculate(new CobLineInput
        {
            LineNumber = 1,
            BilledAmount = 500m,
            SecondaryAllowedAmount = 400m,
            SecondaryMemberResponsibilityBeforeCob = 80m,
            SecondaryPlanPaymentBeforeCob = 320m,
            PrimaryPayerPayment = 480m,
            Model = CobModel.NonDuplication
        });

        // memberResp = max(0, 500 - 480 - 0) = 20
        Assert.Equal(0m, result.SecondaryPlanPayment);
        Assert.Equal(20m, result.MemberResponsibility);
    }

    // ── CalculateAll ──────────────────────────────────────────────────────

    [Fact]
    public void CalculateAll_ProcessesMultipleLines()
    {
        var svc = Make();
        var lines = new[]
        {
            new CobLineInput
            {
                LineNumber = 1, BilledAmount = 200m, SecondaryAllowedAmount = 200m,
                SecondaryMemberResponsibilityBeforeCob = 40m,
                SecondaryPlanPaymentBeforeCob = 160m,
                PrimaryPayerPayment = 100m, Model = CobModel.Complementary
            },
            new CobLineInput
            {
                LineNumber = 2, BilledAmount = 300m, SecondaryAllowedAmount = 300m,
                SecondaryMemberResponsibilityBeforeCob = 60m,
                SecondaryPlanPaymentBeforeCob = 240m,
                PrimaryPayerPayment = 200m, Model = CobModel.Complementary
            }
        };

        var results = svc.CalculateAll(lines);
        Assert.Equal(2, results.Count);
        Assert.Equal(1, results[0].LineNumber);
        Assert.Equal(2, results[1].LineNumber);
    }
}
