using CloudHealthOffice.BenefitEngine.Domain;
using CloudHealthOffice.BenefitEngine.Models;
using CloudHealthOffice.BenefitEngine.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CloudHealthOffice.BenefitEngine.Tests;

/// <summary>
/// Unit tests for the Benefit Calculation Engine.
///
/// These test the core cost-sharing waterfall logic using in-memory
/// test doubles. Each test scenario represents a real adjudication
/// situation a payer encounters in production.
///
/// Test naming convention: Scenario_Condition_ExpectedOutcome
/// </summary>
public class BenefitCalculationEngineTests
{
    // ═══════════════════════════════════════════════════════════════════
    // TEST FIXTURES
    // ═══════════════════════════════════════════════════════════════════

    private static BenefitPlanConfig CreateTestPlan(
        decimal individualDeductible = 500,
        decimal familyDeductible = 1500,
        decimal individualOopMax = 3000,
        decimal familyOopMax = 9000,
        PlanType planType = PlanType.PPO)
    {
        return new BenefitPlanConfig
        {
            Id = Guid.NewGuid(),
            TenantId = "test-tenant",
            PlanName = "Test PPO Plan",
            PlanType = planType,
            PlanYear = "2026",
            IndividualDeductible = individualDeductible,
            FamilyDeductible = familyDeductible,
            IndividualOopMax = individualOopMax,
            FamilyOopMax = familyOopMax,
            FamilyAccumulatorModel = FamilyAccumulatorModel.Embedded,
            Categories =
            [
                // Office visit — $30 copay, 20% coinsurance, deductible applies
                new BenefitCategoryConfig
                {
                    ServiceTypeCode = "98",
                    ServiceTypeDescription = "Professional Physician Visit - Office",
                    IsCovered = true,
                    AuthRequired = false,
                    InNetworkCostSharing =
                    [
                        new CostShareRuleConfig { CostShareType = CostShareType.Deductible, DeductibleApplies = true },
                        new CostShareRuleConfig { CostShareType = CostShareType.Copay, CopayAmount = 30 },
                        new CostShareRuleConfig { CostShareType = CostShareType.Coinsurance, CoinsurancePercent = 0.20m },
                    ],
                    OutOfNetworkCostSharing =
                    [
                        new CostShareRuleConfig { CostShareType = CostShareType.Deductible, DeductibleApplies = true },
                        new CostShareRuleConfig { CostShareType = CostShareType.Coinsurance, CoinsurancePercent = 0.40m },
                    ]
                },

                // Emergency — $250 copay, 20% coinsurance, deductible applies
                new BenefitCategoryConfig
                {
                    ServiceTypeCode = "86",
                    ServiceTypeDescription = "Emergency Services",
                    IsCovered = true,
                    AuthRequired = false,
                    InNetworkCostSharing =
                    [
                        new CostShareRuleConfig { CostShareType = CostShareType.Deductible, DeductibleApplies = true },
                        new CostShareRuleConfig { CostShareType = CostShareType.Copay, CopayAmount = 250 },
                        new CostShareRuleConfig { CostShareType = CostShareType.Coinsurance, CoinsurancePercent = 0.20m },
                    ]
                },

                // Inpatient — no copay, 20% coinsurance, deductible applies, auth required
                new BenefitCategoryConfig
                {
                    ServiceTypeCode = "48",
                    ServiceTypeDescription = "Hospital - Inpatient",
                    IsCovered = true,
                    AuthRequired = true,
                    DayLimit = 30,
                    InNetworkCostSharing =
                    [
                        new CostShareRuleConfig { CostShareType = CostShareType.Deductible, DeductibleApplies = true },
                        new CostShareRuleConfig { CostShareType = CostShareType.Coinsurance, CoinsurancePercent = 0.20m },
                    ]
                },

                // Cosmetic surgery — NOT covered
                new BenefitCategoryConfig
                {
                    ServiceTypeCode = "35",
                    ServiceTypeDescription = "Dental Care",
                    IsCovered = false,
                },

                // Physical therapy — covered, 20 visit limit
                new BenefitCategoryConfig
                {
                    ServiceTypeCode = "BH",
                    ServiceTypeDescription = "Physical Therapy",
                    IsCovered = true,
                    AuthRequired = false,
                    VisitLimit = 20,
                    InNetworkCostSharing =
                    [
                        new CostShareRuleConfig { CostShareType = CostShareType.Copay, CopayAmount = 40 },
                    ]
                }
            ]
        };
    }

    private static BenefitResolutionRequest CreateRequest(
        Guid planId,
        NetworkTier networkTier = NetworkTier.InNetwork,
        bool isEmergency = false,
        params (string code, decimal billed, decimal allowed, string pos)[] lines)
    {
        var request = new BenefitResolutionRequest
        {
            MemberId = "MBR-001",
            SubscriberId = "SUB-001",
            BenefitPlanId = planId,
            ServiceDate = new DateOnly(2026, 3, 8),
            NetworkTier = networkTier,
            IsEmergency = isEmergency,
            Lines = lines.Select((l, i) => new ClaimLineInput
            {
                LineNumber = i + 1,
                ProcedureCode = l.code,
                PlaceOfService = l.pos,
                BilledAmount = l.billed,
                Units = 1
            }).ToList(),
            AllowedAmounts = lines.Select((l, i) => (i + 1, l.allowed))
                .ToDictionary(x => x.Item1, x => x.allowed)
        };
        return request;
    }

    // ═══════════════════════════════════════════════════════════════════
    // TEST: BASIC COST-SHARING WATERFALL
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Simple office visit. Deductible not yet met.
    /// Expected: deductible consumed first, then copay, then coinsurance on remainder.
    ///
    /// Billed: $200, Allowed: $150
    /// Deductible remaining: $500 → applies $150 to deductible (entire allowed amount)
    /// Copay: $30 → but $0 remains after deductible, so copay = $0
    /// Coinsurance: 20% of $0 = $0
    /// Member pays: $150 (all deductible)
    /// Plan pays: $0
    /// </summary>
    [Fact]
    public async Task OfficeVisit_DeductibleNotMet_EntireAllowedAppliedToDeductible()
    {
        // Arrange
        var plan = CreateTestPlan(individualDeductible: 500);
        var engine = CreateEngine(plan, categoryCode: "98");
        var request = CreateRequest(plan.Id,
            lines: ("99213", 200m, 150m, "11"));

        // Act
        var result = await engine.CalculateAsync(request);

        // Assert
        Assert.True(result.Success);
        var line = result.Lines.Single();
        Assert.True(line.IsCovered);
        Assert.Equal(200m, line.BilledAmount);
        Assert.Equal(150m, line.AllowedAmount);
        Assert.Equal(50m, line.ContractualAdjustment); // CO-45: 200 - 150
        Assert.Equal(150m, line.DeductibleAmount);      // All to deductible
        Assert.Equal(0m, line.CopayAmount);              // Nothing left for copay
        Assert.Equal(0m, line.CoinsuranceAmount);        // Nothing left for coinsurance
        Assert.Equal(150m, line.MemberResponsibility);
        Assert.Equal(0m, line.PlanPaidAmount);

        // Verify CAS segments
        Assert.Contains(line.Adjustments, a => a.GroupCode == "CO" && a.ReasonCode == "45" && a.Amount == 50m);
        Assert.Contains(line.Adjustments, a => a.GroupCode == "PR" && a.ReasonCode == "1" && a.Amount == 150m);
    }

    /// <summary>
    /// Office visit where deductible is already met.
    /// Expected: copay + coinsurance only.
    ///
    /// Billed: $200, Allowed: $150
    /// Deductible remaining: $0 (already met)
    /// Copay: $30
    /// Coinsurance: 20% of ($150 - $30) = $24
    /// Member pays: $30 + $24 = $54
    /// Plan pays: $150 - $54 = $96
    /// </summary>
    [Fact]
    public async Task OfficeVisit_DeductibleMet_CopayAndCoinsuranceOnly()
    {
        var plan = CreateTestPlan(individualDeductible: 500);
        var engine = CreateEngine(plan, categoryCode: "98",
            existingDeductible: 500m); // Already met

        var request = CreateRequest(plan.Id,
            lines: ("99213", 200m, 150m, "11"));

        var result = await engine.CalculateAsync(request);

        var line = result.Lines.Single();
        Assert.Equal(0m, line.DeductibleAmount);
        Assert.Equal(30m, line.CopayAmount);
        Assert.Equal(24m, line.CoinsuranceAmount); // 20% of (150 - 30)
        Assert.Equal(54m, line.MemberResponsibility);
        Assert.Equal(96m, line.PlanPaidAmount);
    }

    /// <summary>
    /// Office visit where deductible is partially met.
    /// Expected: remainder of deductible consumed, then copay + coinsurance.
    ///
    /// Billed: $200, Allowed: $150
    /// Deductible remaining: $50 (of $500 total)
    /// → $50 to deductible, $100 remaining
    /// Copay: $30 of remaining $100 → $30
    /// Coinsurance: 20% of ($100 - $30) = $14
    /// Member pays: $50 + $30 + $14 = $94
    /// Plan pays: $150 - $94 = $56
    /// </summary>
    [Fact]
    public async Task OfficeVisit_DeductiblePartiallyMet_PartialDeductibleThenCopayCoinsurance()
    {
        var plan = CreateTestPlan(individualDeductible: 500);
        var engine = CreateEngine(plan, categoryCode: "98",
            existingDeductible: 450m); // $50 remaining

        var request = CreateRequest(plan.Id,
            lines: ("99213", 200m, 150m, "11"));

        var result = await engine.CalculateAsync(request);

        var line = result.Lines.Single();
        Assert.Equal(50m, line.DeductibleAmount);
        Assert.Equal(30m, line.CopayAmount);
        Assert.Equal(14m, line.CoinsuranceAmount); // 20% of (100 - 30)
        Assert.Equal(94m, line.MemberResponsibility);
        Assert.Equal(56m, line.PlanPaidAmount);
    }

    // ═══════════════════════════════════════════════════════════════════
    // TEST: OOP MAX
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// OOP max is about to be reached. Member's total responsibility
    /// should be capped at the remaining OOP amount.
    /// </summary>
    [Fact]
    public async Task OopMaxReached_MemberResponsibilityCapped()
    {
        var plan = CreateTestPlan(individualDeductible: 0, individualOopMax: 3000);
        var engine = CreateEngine(plan, categoryCode: "48",
            existingOop: 2980m); // Only $20 of OOP remaining

        var request = CreateRequest(plan.Id,
            lines: ("99223", 5000m, 3000m, "21"));

        var result = await engine.CalculateAsync(request);

        var line = result.Lines.Single();
        // Coinsurance would be 20% of 3000 = 600, but OOP max caps at $20
        Assert.Equal(20m, line.MemberResponsibility);
        Assert.Equal(2980m, line.PlanPaidAmount); // Plan picks up the rest
        Assert.True(line.OopMaxReduction > 0);
    }

    // ═══════════════════════════════════════════════════════════════════
    // TEST: NON-COVERED SERVICE
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task NonCoveredService_DeniedWithCarc96()
    {
        var plan = CreateTestPlan();
        var engine = CreateEngine(plan, categoryCode: "35"); // Dental — not covered

        var request = CreateRequest(plan.Id,
            lines: ("D0120", 75m, 75m, "81"));

        var result = await engine.CalculateAsync(request);

        var line = result.Lines.Single();
        Assert.False(line.IsCovered);
        Assert.Equal("96", line.DenialReasonCode);
        Assert.Equal(0m, line.PlanPaidAmount);
    }

    // ═══════════════════════════════════════════════════════════════════
    // TEST: VISIT LIMITS
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task VisitLimitExceeded_DeniedWithCarc119()
    {
        var plan = CreateTestPlan();
        var engine = CreateEngine(plan, categoryCode: "BH",
            existingVisitCount: 20); // Limit is 20, already at 20

        var request = CreateRequest(plan.Id,
            lines: ("97110", 150m, 100m, "11"));

        var result = await engine.CalculateAsync(request);

        var line = result.Lines.Single();
        Assert.False(line.IsCovered);
        Assert.Equal("119", line.DenialReasonCode);
    }

    // ═══════════════════════════════════════════════════════════════════
    // TEST: EMERGENCY — NO SURPRISES ACT
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Emergency visit at an out-of-network facility.
    /// Per the No Surprises Act, in-network cost sharing applies.
    /// </summary>
    [Fact]
    public async Task EmergencyOutOfNetwork_InNetworkCostSharingApplied()
    {
        var plan = CreateTestPlan(individualDeductible: 0);
        var engine = CreateEngine(plan, categoryCode: "86");

        // Out-of-network, but emergency
        var request = CreateRequest(plan.Id,
            networkTier: NetworkTier.OutOfNetwork,
            isEmergency: true,
            lines: ("99283", 2000m, 1500m, "23"));

        var result = await engine.CalculateAsync(request);

        var line = result.Lines.Single();
        // Should use in-network cost sharing (copay $250 + 20% coinsurance)
        // not out-of-network (which might be 40% coinsurance, no copay)
        Assert.Equal(250m, line.CopayAmount);
        Assert.Equal(0.20m, line.CoinsurancePercent);
    }

    // ═══════════════════════════════════════════════════════════════════
    // TEST: MULTI-LINE CLAIM — ACCUMULATOR PROGRESSION
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Two-line claim. Deductible should be consumed across both lines,
    /// not applied independently to each.
    /// </summary>
    [Fact]
    public async Task MultiLineClaim_DeductibleSharedAcrossLines()
    {
        var plan = CreateTestPlan(individualDeductible: 200);
        var engine = CreateEngine(plan, categoryCode: "98");

        var request = CreateRequest(plan.Id,
            lines:
            [
                ("99213", 200m, 150m, "11"),  // Line 1: $150 allowed
                ("36415", 50m, 30m, "11"),    // Line 2: $30 allowed
            ]);

        var result = await engine.CalculateAsync(request);

        // Line 1 should consume $150 of the $200 deductible
        var line1 = result.Lines.First(l => l.LineNumber == 1);
        Assert.Equal(150m, line1.DeductibleAmount);

        // Line 2 should consume remaining $30 (only $50 of deductible left, but allowed is $30)
        var line2 = result.Lines.First(l => l.LineNumber == 2);
        Assert.Equal(30m, line2.DeductibleAmount);

        // Total deductible applied: $180 of $200
        Assert.Equal(180m, result.Totals.TotalDeductible);
    }

    // ═══════════════════════════════════════════════════════════════════
    // TEST: CAS SEGMENT GENERATION (for 835)
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task CasSegments_ContractualAndPatientResponsibility_CorrectGroupCodes()
    {
        var plan = CreateTestPlan(individualDeductible: 0);
        var engine = CreateEngine(plan, categoryCode: "98",
            existingDeductible: 500); // Deductible already met

        var request = CreateRequest(plan.Id,
            lines: ("99213", 200m, 150m, "11"));

        var result = await engine.CalculateAsync(request);
        var line = result.Lines.Single();

        // CO-45: contractual adjustment ($200 billed - $150 allowed = $50)
        Assert.Contains(line.Adjustments,
            a => a.GroupCode == "CO" && a.ReasonCode == "45" && a.Amount == 50m);

        // PR-3: copay ($30)
        Assert.Contains(line.Adjustments,
            a => a.GroupCode == "PR" && a.ReasonCode == "3" && a.Amount == 30m);

        // PR-2: coinsurance (20% of $120 = $24)
        Assert.Contains(line.Adjustments,
            a => a.GroupCode == "PR" && a.ReasonCode == "2" && a.Amount == 24m);
    }

    // ═══════════════════════════════════════════════════════════════════
    // TEST HELPER: Create engine with test doubles
    // ═══════════════════════════════════════════════════════════════════

    private static BenefitCalculationEngine CreateEngine(
        BenefitPlanConfig plan,
        string categoryCode,
        decimal existingDeductible = 0,
        decimal existingOop = 0,
        int existingVisitCount = 0)
    {
        var planProvider = new InMemoryBenefitPlanProvider(plan);
        var accumulatorService = new InMemoryAccumulatorService(
            plan, existingDeductible, existingOop, existingVisitCount, categoryCode);
        var categoryResolver = new FixedCategoryResolver(categoryCode,
            plan.GetCategory(categoryCode)?.ServiceTypeDescription ?? "Unknown");

        return new BenefitCalculationEngine(
            categoryResolver, planProvider, accumulatorService,
            NullLogger<BenefitCalculationEngine>.Instance);
    }
}

// ═══════════════════════════════════════════════════════════════════
// TEST DOUBLES
// ═══════════════════════════════════════════════════════════════════

internal class InMemoryBenefitPlanProvider : IBenefitPlanProvider
{
    private readonly BenefitPlanConfig _plan;
    public InMemoryBenefitPlanProvider(BenefitPlanConfig plan) => _plan = plan;
    public Task<BenefitPlanConfig?> GetPlanAsync(Guid benefitPlanId, CancellationToken ct)
        => Task.FromResult<BenefitPlanConfig?>(_plan);
}

internal class InMemoryAccumulatorService : IAccumulatorService
{
    private readonly List<AccumulatorSnapshot> _snapshots = [];
    private readonly List<AccumulatorUpdate> _appliedUpdates = [];

    public InMemoryAccumulatorService(
        BenefitPlanConfig plan,
        decimal existingDeductible,
        decimal existingOop,
        int existingVisitCount,
        string categoryCode)
    {
        _snapshots.Add(new AccumulatorSnapshot
        {
            Type = AccumulatorType.IndividualDeductible,
            Scope = AccumulatorScope.Individual,
            NetworkTier = NetworkTier.InNetwork,
            LimitAmount = plan.IndividualDeductible ?? 0,
            AccumulatedAmountBefore = existingDeductible,
            AccumulatedAmountAfter = existingDeductible,
            RemainingAmount = Math.Max(0, (plan.IndividualDeductible ?? 0) - existingDeductible)
        });

        _snapshots.Add(new AccumulatorSnapshot
        {
            Type = AccumulatorType.FamilyDeductible,
            Scope = AccumulatorScope.Family,
            NetworkTier = NetworkTier.InNetwork,
            LimitAmount = plan.FamilyDeductible ?? 0,
            AccumulatedAmountBefore = existingDeductible,
            AccumulatedAmountAfter = existingDeductible,
            RemainingAmount = Math.Max(0, (plan.FamilyDeductible ?? 0) - existingDeductible)
        });

        _snapshots.Add(new AccumulatorSnapshot
        {
            Type = AccumulatorType.IndividualOutOfPocketMax,
            Scope = AccumulatorScope.Individual,
            NetworkTier = NetworkTier.InNetwork,
            LimitAmount = plan.IndividualOopMax ?? 0,
            AccumulatedAmountBefore = existingOop,
            AccumulatedAmountAfter = existingOop,
            RemainingAmount = Math.Max(0, (plan.IndividualOopMax ?? 0) - existingOop)
        });

        _snapshots.Add(new AccumulatorSnapshot
        {
            Type = AccumulatorType.FamilyOutOfPocketMax,
            Scope = AccumulatorScope.Family,
            NetworkTier = NetworkTier.InNetwork,
            LimitAmount = plan.FamilyOopMax ?? 0,
            AccumulatedAmountBefore = existingOop,
            AccumulatedAmountAfter = existingOop,
            RemainingAmount = Math.Max(0, (plan.FamilyOopMax ?? 0) - existingOop)
        });

        if (existingVisitCount > 0)
        {
            _snapshots.Add(new AccumulatorSnapshot
            {
                Type = AccumulatorType.VisitCount,
                Scope = AccumulatorScope.Individual,
                NetworkTier = NetworkTier.InNetwork,
                LimitAmount = 0,
                AccumulatedAmountBefore = existingVisitCount,
                AccumulatedAmountAfter = existingVisitCount,
                RemainingAmount = 0
            });
        }
    }

    public Task<IReadOnlyList<AccumulatorSnapshot>> GetAccumulatorsAsync(
        string memberId, string subscriberId, Guid benefitPlanId,
        string planYear, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<AccumulatorSnapshot>>(_snapshots);

    public Task ApplyUpdatesAsync(string memberId, string subscriberId, 
        Guid benefitPlanId, string planYear,
        string claimId,
        IReadOnlyList<AccumulatorUpdate> updates, CancellationToken ct = default)
    {
        _appliedUpdates.AddRange(updates);
        return Task.CompletedTask;
    }

    public Task ReverseAsync(string memberId, string subscriberId,
        Guid benefitPlanId, string planYear, string claimId, CancellationToken ct)
        => Task.CompletedTask;

    public Task ResetForPlanYearAsync(Guid benefitPlanId, string planYear, CancellationToken ct)
        => Task.CompletedTask;
}

/// <summary>
/// Test double that always resolves to the configured service type code.
/// </summary>
internal class FixedCategoryResolver : IServiceCategoryResolver
{
    private readonly string _code;
    private readonly string _description;

    public FixedCategoryResolver(string code, string description)
    {
        _code = code;
        _description = description;
    }

    public Task<ServiceCategoryMatch?> ResolveAsync(
        string tenantId, Guid benefitPlanId, string procedureCode,
        string codeType, string placeOfService, IReadOnlyList<string> modifiers,
        string? revenueCode, CancellationToken ct)
    {
        return Task.FromResult<ServiceCategoryMatch?>(new ServiceCategoryMatch
        {
            ServiceTypeCode = _code,
            ServiceTypeDescription = _description,
            MatchedBy = "TestFixture",
            MatchedRule = $"Fixed:{_code}"
        });
    }
}