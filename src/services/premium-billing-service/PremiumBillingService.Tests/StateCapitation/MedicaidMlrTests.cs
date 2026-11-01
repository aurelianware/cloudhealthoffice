using PremiumBillingService.Mlr;

namespace PremiumBillingService.Tests.StateCapitation;

/// <summary>The 42 CFR 438.8 MLR for Texas and Medi-Cal (gap 4 of the Medicaid core assessment).</summary>
public class MedicaidMlrTests
{
    private static MlrInputs Inputs(decimal claims, int memberMonths, decimal fraud = 0m) => new()
    {
        IncurredClaims = claims,
        QualityImprovement = 1_000_000m,
        FraudPrevention = fraud,
        PremiumRevenue = 104_000_000m,
        TaxesAndFees = 4_000_000m,
        MemberMonths = memberMonths
    };

    [Fact]
    public void FullyCredible_MeetingTheMinimum_OwesNothing()
    {
        var r = MedicaidMlrCalculator.Calculate(Inputs(86_000_000m, 400_000), MlrStandard.California);

        r.AdjustedPremiumRevenue.Should().Be(100_000_000m);
        r.UnadjustedMlr.Should().Be(0.87m);
        r.Credibility.Should().Be(MlrCredibility.FullyCredible);
        r.MeetsMinimum.Should().BeTrue();
        r.Remittance.Should().Be(0m);
    }

    [Fact]
    public void MediCal_Shortfall_IsRemittedOnAdjustedPremium()
    {
        var r = MedicaidMlrCalculator.Calculate(Inputs(80_000_000m, 400_000), MlrStandard.California);

        r.AdjustedMlr.Should().Be(0.81m);
        r.MeetsMinimum.Should().BeFalse();
        r.Remittance.Should().Be(4_000_000m);
    }

    [Fact]
    public void Texas_Shortfall_IsReported_WithoutARemittanceHere()
    {
        var r = MedicaidMlrCalculator.Calculate(Inputs(80_000_000m, 400_000), MlrStandard.Texas);

        r.MeetsMinimum.Should().BeFalse();
        r.Remittance.Should().Be(0m);
        r.Notes.Should().Contain(n => n.Contains("no remittance"));
    }

    [Fact]
    public void PartialCredibility_AddsTheInterpolatedAdjustment()
    {
        // 75,000 member-months: halfway between 50,000 (2.7%) and 100,000 (1.9%).
        var r = MedicaidMlrCalculator.Calculate(Inputs(82_500_000m, 75_000), MlrStandard.California);

        r.Credibility.Should().Be(MlrCredibility.PartiallyCredible);
        r.CredibilityAdjustment.Should().Be(0.023m);
        r.AdjustedMlr.Should().Be(0.835m + 0.023m);
        r.MeetsMinimum.Should().BeTrue();
    }

    [Fact]
    public void NonCredible_HasNoStandard()
    {
        var r = MedicaidMlrCalculator.Calculate(Inputs(50_000_000m, 3_000), MlrStandard.California);

        r.Credibility.Should().Be(MlrCredibility.NonCredible);
        r.MeetsMinimum.Should().BeTrue();
        r.Remittance.Should().Be(0m);
    }

    [Fact]
    public void FraudPrevention_IsCappedAtHalfAPercent()
    {
        var r = MedicaidMlrCalculator.Calculate(Inputs(84_000_000m, 400_000, fraud: 900_000m), MlrStandard.Texas);

        r.FraudPreventionCounted.Should().Be(500_000m);
        r.Numerator.Should().Be(85_500_000m);
    }
}
