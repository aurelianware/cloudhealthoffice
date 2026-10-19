using CloudHealthOffice.BenefitEngine.Domain;
using CloudHealthOffice.BenefitEngine.Services;
using Xunit;

namespace CloudHealthOffice.BenefitEngine.Tests;

/// <summary>
/// Coinsurance is money and is rounded to the cent half away from zero
/// (MoneyRounding.ToCents), not with banker's rounding.
/// </summary>
public partial class BenefitCalculationEngineTests
{
    private const string MidpointCategory = "MPT";

    /// <summary>
    /// A plan with one category: $10 copay in <paramref name="mode"/>, deductible, 15% coinsurance.
    /// </summary>
    private static BenefitPlanConfig CreateMidpointPlan(CopayApplicationMode mode)
    {
        var plan = CreateTestPlan(individualDeductible: 500);
        plan.Categories.Add(new BenefitCategoryConfig
        {
            ServiceTypeCode = MidpointCategory,
            ServiceTypeDescription = "Midpoint rounding",
            IsCovered = true,
            AuthRequired = false,
            InNetworkCostSharing =
            [
                new CostShareRuleConfig { CostShareType = CostShareType.Deductible, DeductibleApplies = true },
                new CostShareRuleConfig { CostShareType = CostShareType.Copay, CopayAmount = 10, CopayApplicationMode = mode },
                new CostShareRuleConfig { CostShareType = CostShareType.Coinsurance, CoinsurancePercent = 0.15m },
            ]
        });
        return plan;
    }

    // Allowed $110.30, deductible met, $10 copay: coinsurance is 15% of $100.30 = $15.045.
    // Half away from zero gives $15.05; banker's rounding gave $15.04.
    [Theory]
    [InlineData(CopayApplicationMode.AfterDeductible)]
    [InlineData(CopayApplicationMode.InAdditionToDeductible)]
    [InlineData(CopayApplicationMode.InsteadOfDeductible)]
    public async Task Coinsurance_AtHalfCent_RoundsAwayFromZero(CopayApplicationMode mode)
    {
        var plan = CreateMidpointPlan(mode);
        var engine = CreateEngine(plan, categoryCode: MidpointCategory, existingDeductible: 500m);
        var request = CreateRequest(plan.Id, lines: ("99213", 200m, 110.30m, "11"));

        var result = await engine.CalculateAsync(request);

        Assert.True(result.Success);
        var line = result.Lines.Single();
        Assert.Equal(0m, line.DeductibleAmount);
        Assert.Equal(10m, line.CopayAmount);
        Assert.Equal(15.05m, line.CoinsuranceAmount);
        Assert.Equal(25.05m, line.MemberResponsibility);
        Assert.Equal(85.25m, line.PlanPaidAmount);
    }
}
