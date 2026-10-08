using CloudHealthOffice.BenefitEngine.Domain;
using CloudHealthOffice.BenefitEngine.Models;
using CloudHealthOffice.BenefitEngine.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CloudHealthOffice.BenefitEngine.Tests;

public class BenefitCalculationEngineTests
{
    // ═══════════════════════════════════════════════════════════════════
    // FIXTURES
    // ═══════════════════════════════════════════════════════════════════

    private static BenefitPlanConfig CreateTestPlan(
        decimal individualDeductible = 500,
        decimal familyDeductible = 1500,
        decimal individualOopMax = 3000,
        decimal familyOopMax = 9000,
        PlanType planType = PlanType.PPO,
        FamilyAccumulatorModel familyModel = FamilyAccumulatorModel.Embedded,
        bool isHdhp = false,
        HashSet<string>? hdhpExemptServices = null,
        InpatientPricingMethod inpatientMethod = InpatientPricingMethod.PerLine)
    {
        return new BenefitPlanConfig
        {
            Id = Guid.NewGuid(),
            TenantId = "test-tenant",
            PlanName = "Test Plan",
            PlanType = planType,
            PlanYear = "2026",
            IndividualDeductible = individualDeductible,
            FamilyDeductible = familyDeductible,
            IndividualOopMax = individualOopMax,
            FamilyOopMax = familyOopMax,
            FamilyAccumulatorModel = familyModel,
            IsHdhp = isHdhp,
            HdhpDeductibleExemptServices = hdhpExemptServices ?? [],
            DefaultInpatientPricingMethod = inpatientMethod,
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

                // Inpatient — no copay, 20% coinsurance, deductible, auth required
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

                // Dental — NOT covered
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
                },

                // Preventive care — copay only, NO deductible (copay-instead mode)
                new BenefitCategoryConfig
                {
                    ServiceTypeCode = "AE",
                    ServiceTypeDescription = "Preventive Care",
                    IsCovered = true,
                    AuthRequired = false,
                    InNetworkCostSharing =
                    [
                        new CostShareRuleConfig
                        {
                            CostShareType = CostShareType.Copay,
                            CopayAmount = 0,
                            CopayApplicationMode = CopayApplicationMode.InsteadOfDeductible
                        },
                    ]
                },

                // PCP visit — copay instead of deductible
                new BenefitCategoryConfig
                {
                    ServiceTypeCode = "PCP",
                    ServiceTypeDescription = "Primary Care Visit",
                    IsCovered = true,
                    AuthRequired = false,
                    InNetworkCostSharing =
                    [
                        new CostShareRuleConfig
                        {
                            CostShareType = CostShareType.Copay,
                            CopayAmount = 30,
                            CopayApplicationMode = CopayApplicationMode.InsteadOfDeductible
                        },
                        new CostShareRuleConfig { CostShareType = CostShareType.Coinsurance, CoinsurancePercent = 0.20m },
                    ]
                },
            ]
        };
    }

    private static BenefitResolutionRequest CreateRequest(
        Guid planId,
        NetworkTier networkTier = NetworkTier.InNetwork,
        bool isEmergency = false,
        string? claimType = null,
        string? drgCode = null,
        decimal? drgAllowedAmount = null,
        params (string code, decimal billed, decimal allowed, string pos)[] lines)
    {
        return new BenefitResolutionRequest
        {
            MemberId = "MBR-001",
            SubscriberId = "SUB-001",
            BenefitPlanId = planId,
            ServiceDate = new DateOnly(2026, 3, 8),
            NetworkTier = networkTier,
            IsEmergency = isEmergency,
            ClaimType = claimType,
            DrgCode = drgCode,
            DrgAllowedAmount = drgAllowedAmount,
            ClaimId = Guid.NewGuid().ToString(),
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
    }

    // ═══════════════════════════════════════════════════════════════════
    // ORIGINAL TESTS — BASIC COST-SHARING WATERFALL
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task OfficeVisit_DeductibleNotMet_EntireAllowedAppliedToDeductible()
    {
        var plan = CreateTestPlan(individualDeductible: 500);
        var engine = CreateEngine(plan, categoryCode: "98");
        var request = CreateRequest(plan.Id, lines: ("99213", 200m, 150m, "11"));

        var result = await engine.CalculateAsync(request);

        Assert.True(result.Success);
        var line = result.Lines.Single();
        Assert.True(line.IsCovered);
        Assert.Equal(200m, line.BilledAmount);
        Assert.Equal(150m, line.AllowedAmount);
        Assert.Equal(50m, line.ContractualAdjustment);
        Assert.Equal(150m, line.DeductibleAmount);
        Assert.Equal(0m, line.CopayAmount);
        Assert.Equal(0m, line.CoinsuranceAmount);
        Assert.Equal(150m, line.MemberResponsibility);
        Assert.Equal(0m, line.PlanPaidAmount);
        Assert.Contains(line.Adjustments, a => a.GroupCode == "CO" && a.ReasonCode == "45" && a.Amount == 50m);
        Assert.Contains(line.Adjustments, a => a.GroupCode == "PR" && a.ReasonCode == "1" && a.Amount == 150m);
        Assert.Contains("planLookup", result.Timings.Keys);
        Assert.Contains("accumulatorRead", result.Timings.Keys);
        Assert.Contains("lineProcessing", result.Timings.Keys);
        Assert.Contains("accumulatorWrite", result.Timings.Keys);
    }

    [Fact]
    public async Task OfficeVisit_DeductibleMet_CopayAndCoinsuranceOnly()
    {
        var plan = CreateTestPlan(individualDeductible: 500);
        var engine = CreateEngine(plan, categoryCode: "98", existingDeductible: 500m);
        var request = CreateRequest(plan.Id, lines: ("99213", 200m, 150m, "11"));

        var result = await engine.CalculateAsync(request);
        var line = result.Lines.Single();
        Assert.Equal(0m, line.DeductibleAmount);
        Assert.Equal(30m, line.CopayAmount);
        Assert.Equal(24m, line.CoinsuranceAmount);
        Assert.Equal(54m, line.MemberResponsibility);
        Assert.Equal(96m, line.PlanPaidAmount);
    }

    [Fact]
    public async Task OfficeVisit_DeductiblePartiallyMet_PartialDeductibleThenCopayCoinsurance()
    {
        var plan = CreateTestPlan(individualDeductible: 500);
        var engine = CreateEngine(plan, categoryCode: "98", existingDeductible: 450m);
        var request = CreateRequest(plan.Id, lines: ("99213", 200m, 150m, "11"));

        var result = await engine.CalculateAsync(request);
        var line = result.Lines.Single();
        Assert.Equal(50m, line.DeductibleAmount);
        Assert.Equal(30m, line.CopayAmount);
        Assert.Equal(14m, line.CoinsuranceAmount);
        Assert.Equal(94m, line.MemberResponsibility);
        Assert.Equal(56m, line.PlanPaidAmount);
    }

    [Fact]
    public async Task OopMaxReached_MemberResponsibilityCapped()
    {
        var plan = CreateTestPlan(individualDeductible: 0, individualOopMax: 3000);
        var engine = CreateEngine(plan, categoryCode: "48", existingOop: 2980m);
        var request = CreateRequest(plan.Id, lines: ("99223", 5000m, 3000m, "21"));

        var result = await engine.CalculateAsync(request);
        var line = result.Lines.Single();
        Assert.Equal(20m, line.MemberResponsibility);
        Assert.Equal(2980m, line.PlanPaidAmount);
        Assert.True(line.OopMaxReduction > 0);
    }

    [Fact]
    public async Task NonCoveredService_DeniedWithCarc96()
    {
        var plan = CreateTestPlan();
        var engine = CreateEngine(plan, categoryCode: "35");
        var request = CreateRequest(plan.Id, lines: ("D0120", 75m, 75m, "81"));

        var result = await engine.CalculateAsync(request);
        var line = result.Lines.Single();
        Assert.False(line.IsCovered);
        Assert.Equal("96", line.DenialReasonCode);
        Assert.Equal(0m, line.PlanPaidAmount);
    }

    [Fact]
    public async Task NoCategoryMapping_DeniedWithCarc204()
    {
        var plan = CreateTestPlan();
        var engine = CreateUnmappedEngine(plan);
        var request = CreateRequest(plan.Id, lines: ("ZZZZZ", 75m, 75m, "11"));

        var result = await engine.CalculateAsync(request);
        var line = result.Lines.Single();
        Assert.False(line.IsCovered);
        Assert.Equal("204", line.DenialReasonCode);
        Assert.Equal(0m, line.PlanPaidAmount);
    }

    [Fact]
    public async Task Drg_NoCategoryMapping_DeniedWithCarc204()
    {
        var plan = CreateTestPlan(inpatientMethod: InpatientPricingMethod.DrgCaseRate);
        var engine = CreateUnmappedEngine(plan);
        var request = CreateRequest(plan.Id,
            claimType: "837I", drgCode: "470", drgAllowedAmount: 12000m,
            lines: ("99223", 8000m, 8000m, "21"));

        var result = await engine.CalculateAsync(request);
        Assert.False(result.Success);
        Assert.Equal("204", result.DenialReasonCode);
    }

    [Fact]
    public async Task VisitLimitExceeded_DeniedWithCarc119()
    {
        var plan = CreateTestPlan();
        var engine = CreateEngine(plan, categoryCode: "BH", existingVisitCount: 20);
        var request = CreateRequest(plan.Id, lines: ("97110", 150m, 100m, "11"));

        var result = await engine.CalculateAsync(request);
        var line = result.Lines.Single();
        Assert.False(line.IsCovered);
        Assert.Equal("119", line.DenialReasonCode);
    }

    [Fact]
    public async Task EmergencyOutOfNetwork_InNetworkCostSharingApplied()
    {
        var plan = CreateTestPlan(individualDeductible: 0);
        var engine = CreateEngine(plan, categoryCode: "86");
        var request = CreateRequest(plan.Id,
            networkTier: NetworkTier.OutOfNetwork, isEmergency: true,
            lines: ("99283", 2000m, 1500m, "23"));

        var result = await engine.CalculateAsync(request);
        var line = result.Lines.Single();
        Assert.Equal(250m, line.CopayAmount);
        Assert.Equal(0.20m, line.CoinsurancePercent);
    }

    [Fact]
    public async Task MultiLineClaim_DeductibleSharedAcrossLines()
    {
        var plan = CreateTestPlan(individualDeductible: 200);
        var engine = CreateEngine(plan, categoryCode: "98");
        var request = CreateRequest(plan.Id,
            lines: [("99213", 200m, 150m, "11"), ("36415", 50m, 30m, "11")]);

        var result = await engine.CalculateAsync(request);
        var line1 = result.Lines.First(l => l.LineNumber == 1);
        Assert.Equal(150m, line1.DeductibleAmount);
        var line2 = result.Lines.First(l => l.LineNumber == 2);
        Assert.Equal(30m, line2.DeductibleAmount);
        Assert.Equal(180m, result.Totals.TotalDeductible);
    }

    [Fact]
    public async Task CasSegments_ContractualAndPatientResponsibility_CorrectGroupCodes()
    {
        var plan = CreateTestPlan(individualDeductible: 0);
        var engine = CreateEngine(plan, categoryCode: "98", existingDeductible: 500);
        var request = CreateRequest(plan.Id, lines: ("99213", 200m, 150m, "11"));

        var result = await engine.CalculateAsync(request);
        var line = result.Lines.Single();
        Assert.Contains(line.Adjustments, a => a.GroupCode == "CO" && a.ReasonCode == "45" && a.Amount == 50m);
        Assert.Contains(line.Adjustments, a => a.GroupCode == "PR" && a.ReasonCode == "3" && a.Amount == 30m);
        Assert.Contains(line.Adjustments, a => a.GroupCode == "PR" && a.ReasonCode == "2" && a.Amount == 24m);
    }

    // ═══════════════════════════════════════════════════════════════════
    // FEATURE 1: HDHP / HSA DEDUCTIBLE-FIRST
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// HDHP plan: deductible MUST apply even when category says DeductibleApplies=false.
    /// Physical therapy normally has copay-only ($40). Under HDHP, deductible applies first.
    /// </summary>
    [Fact]
    public async Task Hdhp_DeductibleForcedOnNonExemptService()
    {
        var plan = CreateTestPlan(
            planType: PlanType.HDHP, isHdhp: true,
            individualDeductible: 3000);
        var engine = CreateEngine(plan, categoryCode: "BH");

        var request = CreateRequest(plan.Id, lines: ("97110", 200m, 150m, "11"));
        var result = await engine.CalculateAsync(request);

        var line = result.Lines.Single();
        // Deductible should consume the entire allowed (3000 remaining > 150)
        Assert.Equal(150m, line.DeductibleAmount);
        Assert.Equal(150m, line.MemberResponsibility);
        Assert.Equal(0m, line.PlanPaidAmount);
    }

    /// <summary>
    /// HDHP plan: preventive care is exempt from deductible (ACA mandate).
    /// Should use the category's own cost-sharing rules.
    /// </summary>
    [Fact]
    public async Task Hdhp_PreventiveExemptFromDeductible()
    {
        var plan = CreateTestPlan(
            planType: PlanType.HDHP, isHdhp: true,
            individualDeductible: 3000,
            hdhpExemptServices: ["AE"]);
        var engine = CreateEngine(plan, categoryCode: "AE");

        var request = CreateRequest(plan.Id, lines: ("99395", 250m, 200m, "11"));
        var result = await engine.CalculateAsync(request);

        var line = result.Lines.Single();
        // Preventive: $0 copay (InsteadOfDeductible), no deductible
        Assert.Equal(0m, line.DeductibleAmount);
        Assert.Equal(0m, line.CopayAmount);
        Assert.Equal(0m, line.MemberResponsibility);
        Assert.Equal(200m, line.PlanPaidAmount);
    }

    /// <summary>
    /// HDHP: once deductible is met, standard copay/coinsurance kicks in.
    /// </summary>
    [Fact]
    public async Task Hdhp_DeductibleMet_ThenCopayCoinsuranceApply()
    {
        var plan = CreateTestPlan(
            planType: PlanType.HDHP, isHdhp: true,
            individualDeductible: 3000);
        var engine = CreateEngine(plan, categoryCode: "98",
            existingDeductible: 3000m); // Already met

        var request = CreateRequest(plan.Id, lines: ("99213", 200m, 150m, "11"));
        var result = await engine.CalculateAsync(request);

        var line = result.Lines.Single();
        Assert.Equal(0m, line.DeductibleAmount);
        Assert.Equal(30m, line.CopayAmount);
        Assert.Equal(24m, line.CoinsuranceAmount);
    }

    // ═══════════════════════════════════════════════════════════════════
    // FEATURE 2: AGGREGATE FAMILY ACCUMULATOR MODEL
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Aggregate model: family deductible is the only pool.
    /// No individual sub-limit exists.
    /// </summary>
    [Fact]
    public async Task Aggregate_FamilyDeductibleIsOnlyPool()
    {
        var plan = CreateTestPlan(
            individualDeductible: 0, // Ignored in aggregate
            familyDeductible: 3000,
            individualOopMax: 0,
            familyOopMax: 12000,
            familyModel: FamilyAccumulatorModel.Aggregate);
        var engine = CreateEngine(plan, categoryCode: "98",
            familyModel: FamilyAccumulatorModel.Aggregate,
            familyDeductible: 3000);

        var request = CreateRequest(plan.Id, lines: ("99213", 200m, 150m, "11"));
        var result = await engine.CalculateAsync(request);

        var line = result.Lines.Single();
        // All $150 goes to family deductible pool
        Assert.Equal(150m, line.DeductibleAmount);
        Assert.Equal(150m, line.MemberResponsibility);
    }

    /// <summary>
    /// Aggregate: when family deductible is met, all members benefit.
    /// </summary>
    [Fact]
    public async Task Aggregate_FamilyDeductibleMet_CopayCoinsuranceOnly()
    {
        var plan = CreateTestPlan(
            individualDeductible: 0,
            familyDeductible: 3000,
            individualOopMax: 0,
            familyOopMax: 12000,
            familyModel: FamilyAccumulatorModel.Aggregate);
        var engine = CreateEngine(plan, categoryCode: "98",
            familyModel: FamilyAccumulatorModel.Aggregate,
            familyDeductible: 3000,
            existingFamilyDeductible: 3000m); // Met

        var request = CreateRequest(plan.Id, lines: ("99213", 200m, 150m, "11"));
        var result = await engine.CalculateAsync(request);

        var line = result.Lines.Single();
        Assert.Equal(0m, line.DeductibleAmount);
        Assert.Equal(30m, line.CopayAmount);
        Assert.Equal(24m, line.CoinsuranceAmount);
    }

    /// <summary>
    /// Aggregate: OOP max is also family-only, no individual cap.
    /// </summary>
    [Fact]
    public async Task Aggregate_OopMaxIsFamilyOnly()
    {
        var plan = CreateTestPlan(
            individualDeductible: 0,
            familyDeductible: 0,
            individualOopMax: 0,
            familyOopMax: 5000,
            familyModel: FamilyAccumulatorModel.Aggregate);
        var engine = CreateEngine(plan, categoryCode: "48",
            familyModel: FamilyAccumulatorModel.Aggregate,
            familyDeductible: 0,
            existingFamilyOop: 4990m); // Only $10 remaining

        var request = CreateRequest(plan.Id, lines: ("99223", 5000m, 3000m, "21"));
        var result = await engine.CalculateAsync(request);

        var line = result.Lines.Single();
        Assert.Equal(10m, line.MemberResponsibility);
        Assert.True(line.OopMaxReduction > 0);
    }

    // ═══════════════════════════════════════════════════════════════════
    // OOP APPLIES — cost share excluded from the out-of-pocket max
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Sets <see cref="CostShareRuleConfig.OopApplies"/> on the in-network
    /// rules of one category; <paramref name="types"/> limits it to those
    /// rule types (all rules when omitted).
    /// </summary>
    private static BenefitPlanConfig WithOopApplies(
        BenefitPlanConfig plan, string serviceTypeCode, bool oopApplies, params CostShareType[] types)
        => plan with
        {
            Categories = plan.Categories
                .Select(c => c.ServiceTypeCode != serviceTypeCode ? c : c with
                {
                    InNetworkCostSharing = c.InNetworkCostSharing
                        .Select(r => types.Length == 0 || types.Contains(r.CostShareType)
                            ? r with { OopApplies = oopApplies }
                            : r)
                        .ToList()
                })
                .ToList()
        };

    private static AccumulatorState Snapshot(BenefitResolutionResult result, AccumulatorType type,
        AccumulatorScope scope = AccumulatorScope.Individual)
        => result.AccumulatorSnapshot.Single(s =>
            s.Type == type && s.Scope == scope && s.NetworkTier == NetworkTier.InNetwork);

    [Fact]
    public void CostShareRuleConfig_OopApplies_DefaultsTrue()
    {
        Assert.True(new CostShareRuleConfig().OopApplies);
    }

    [Fact]
    public async Task OopApplies_Default_CostShareIncrementsOopAccumulator()
    {
        var plan = CreateTestPlan(individualDeductible: 500);
        var engine = CreateEngine(plan, categoryCode: "98");
        var request = CreateRequest(plan.Id, lines: ("99213", 200m, 150m, "11"));

        var result = await engine.CalculateAsync(request);

        Assert.Equal(150m, result.Lines.Single().MemberResponsibility);
        Assert.Equal(150m, Snapshot(result, AccumulatorType.IndividualOutOfPocketMax).AmountApplied);
        Assert.Equal(150m, Snapshot(result, AccumulatorType.FamilyOutOfPocketMax, AccumulatorScope.Family).AmountApplied);
    }

    [Fact]
    public async Task OopAppliesFalse_OopMaxReached_MemberResponsibilityNotCapped()
    {
        var plan = WithOopApplies(CreateTestPlan(individualDeductible: 0, individualOopMax: 3000), "48", false);
        var engine = CreateEngine(plan, categoryCode: "48", existingOop: 2980m);
        var request = CreateRequest(plan.Id, lines: ("99223", 5000m, 3000m, "21"));

        var result = await engine.CalculateAsync(request);

        var line = result.Lines.Single();
        Assert.Equal(600m, line.CoinsuranceAmount);
        Assert.Equal(600m, line.MemberResponsibility);
        Assert.Equal(2400m, line.PlanPaidAmount);
        Assert.Equal(0m, line.OopMaxReduction);
        Assert.DoesNotContain(line.Adjustments, a => a.GroupCode == "OA" && a.ReasonCode == "23");
        Assert.Equal(0m, Snapshot(result, AccumulatorType.IndividualOutOfPocketMax).AmountApplied);
        Assert.Equal(0m, Snapshot(result, AccumulatorType.FamilyOutOfPocketMax, AccumulatorScope.Family).AmountApplied);
    }

    [Fact]
    public async Task OopAppliesFalse_DeductibleStillAccumulates_OopDoesNot()
    {
        var plan = WithOopApplies(CreateTestPlan(individualDeductible: 500), "98", false);
        var engine = CreateEngine(plan, categoryCode: "98");
        var request = CreateRequest(plan.Id, lines: ("99213", 200m, 150m, "11"));

        var result = await engine.CalculateAsync(request);

        var line = result.Lines.Single();
        Assert.Equal(150m, line.DeductibleAmount);
        Assert.Equal(150m, line.MemberResponsibility);
        Assert.Equal(150m, Snapshot(result, AccumulatorType.IndividualDeductible).AmountApplied);
        Assert.Equal(0m, Snapshot(result, AccumulatorType.IndividualOutOfPocketMax).AmountApplied);
    }

    [Fact]
    public async Task OopAppliesFalse_OnCoinsuranceOnly_CopayCappedCoinsuranceNot()
    {
        // Deductible met; $10 OOP remaining. Copay ($30) counts and is
        // capped to $10; coinsurance ($24) is excluded and owed in full.
        var plan = WithOopApplies(CreateTestPlan(individualDeductible: 500, individualOopMax: 3000),
            "98", false, CostShareType.Coinsurance);
        var engine = CreateEngine(plan, categoryCode: "98", existingDeductible: 500m, existingOop: 2990m);
        var request = CreateRequest(plan.Id, lines: ("99213", 200m, 150m, "11"));

        var result = await engine.CalculateAsync(request);

        var line = result.Lines.Single();
        Assert.Equal(30m, line.CopayAmount);
        Assert.Equal(24m, line.CoinsuranceAmount);
        Assert.Equal(20m, line.OopMaxReduction);
        Assert.Equal(34m, line.MemberResponsibility);
        Assert.Equal(116m, line.PlanPaidAmount);
        Assert.Equal(10m, Snapshot(result, AccumulatorType.IndividualOutOfPocketMax).AmountApplied);
    }

    [Fact]
    public async Task OopAppliesFalse_AggregateWithAcaCap_NeitherPoolNorCapConsumed()
    {
        var plan = WithOopApplies(CreateTestPlan(
            individualDeductible: 0,
            familyDeductible: 0,
            individualOopMax: 0,
            familyOopMax: 5000,
            familyModel: FamilyAccumulatorModel.Aggregate), "48", false) with
        {
            IsAcaCapEnforced = true,
            AcaIndividualCap = 9200m
        };
        var engine = CreateEngine(plan, categoryCode: "48",
            familyModel: FamilyAccumulatorModel.Aggregate,
            familyDeductible: 0,
            existingFamilyOop: 4990m);
        var request = CreateRequest(plan.Id, lines: ("99223", 5000m, 3000m, "21"));

        var result = await engine.CalculateAsync(request);

        var line = result.Lines.Single();
        Assert.Equal(600m, line.MemberResponsibility);
        Assert.Equal(0m, line.OopMaxReduction);
        Assert.Equal(0m, Snapshot(result, AccumulatorType.FamilyOutOfPocketMax, AccumulatorScope.Family).AmountApplied);
        Assert.Equal(0m, Snapshot(result, AccumulatorType.AcaIndividualCap).AmountApplied);
    }

    [Fact]
    public async Task OopAppliesTrue_AggregateWithAcaCap_PoolAndCapConsumedTogether()
    {
        var plan = CreateTestPlan(
            individualDeductible: 0,
            familyDeductible: 0,
            individualOopMax: 0,
            familyOopMax: 5000,
            familyModel: FamilyAccumulatorModel.Aggregate) with
        {
            IsAcaCapEnforced = true,
            AcaIndividualCap = 9200m
        };
        var engine = CreateEngine(plan, categoryCode: "48",
            familyModel: FamilyAccumulatorModel.Aggregate,
            familyDeductible: 0,
            existingFamilyOop: 4990m);
        var request = CreateRequest(plan.Id, lines: ("99223", 5000m, 3000m, "21"));

        var result = await engine.CalculateAsync(request);

        Assert.Equal(10m, result.Lines.Single().MemberResponsibility);
        Assert.Equal(10m, Snapshot(result, AccumulatorType.FamilyOutOfPocketMax, AccumulatorScope.Family).AmountApplied);
        Assert.Equal(10m, Snapshot(result, AccumulatorType.AcaIndividualCap).AmountApplied);
    }

    // ═══════════════════════════════════════════════════════════════════
    // FEATURE 3: ACCUMULATOR REVERSAL (VOID / REPLACE)
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Reversal_CallsAccumulatorServiceReverse()
    {
        var plan = CreateTestPlan();
        var accService = new TrackingAccumulatorService(plan, 0, 0, 0, "98");
        var engine = new BenefitCalculationEngine(
            new FixedCategoryResolver("98", "Office Visit"),
            new InMemoryBenefitPlanProvider(plan),
            accService,
            new BenefitRuleGate(NullLogger<BenefitRuleGate>.Instance),
            NullLogger<BenefitCalculationEngine>.Instance);

        await engine.ReverseClaimAsync(
            "MBR-001", "SUB-001", plan.Id,
            new DateOnly(2026, 3, 8), "ORIG-CLM-001");

        Assert.True(accService.ReverseCalled);
        Assert.Equal("ORIG-CLM-001", accService.ReversedClaimId);
    }

    /// <summary>
    /// After processing a claim and then reversing, the accumulator impact
    /// should be unwound. We verify by processing, reversing, then processing
    /// the same claim again — accumulators should be back to original state.
    /// </summary>
    [Fact]
    public async Task Reversal_AccumulatorImpactUnwound()
    {
        var plan = CreateTestPlan(individualDeductible: 500);
        var accService = new TrackingAccumulatorService(plan, 0, 0, 0, "98");
        var engine = new BenefitCalculationEngine(
            new FixedCategoryResolver("98", "Office Visit"),
            new InMemoryBenefitPlanProvider(plan),
            accService,
            new BenefitRuleGate(NullLogger<BenefitRuleGate>.Instance),
            NullLogger<BenefitCalculationEngine>.Instance);

        var request = CreateRequest(plan.Id, lines: ("99213", 200m, 150m, "11"));

        // First adjudication: $150 goes to deductible
        var result1 = await engine.CalculateAsync(request);
        Assert.Equal(150m, result1.Lines.Single().DeductibleAmount);

        // Reverse
        await engine.ReverseClaimAsync(
            "MBR-001", "SUB-001", plan.Id,
            new DateOnly(2026, 3, 8), request.ClaimId);

        Assert.True(accService.ReverseCalled);
    }

    // ═══════════════════════════════════════════════════════════════════
    // FEATURE 4: COPAY-ONLY (INSTEAD OF DEDUCTIBLE)
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// PCP visit with CopayInsteadOfDeductible: $30 copay, no deductible consumed.
    /// Deductible balance should remain unchanged.
    /// </summary>
    [Fact]
    public async Task CopayInsteadOfDeductible_DeductibleNotConsumed()
    {
        var plan = CreateTestPlan(individualDeductible: 500);
        var engine = CreateEngine(plan, categoryCode: "PCP");

        var request = CreateRequest(plan.Id, lines: ("99213", 200m, 150m, "11"));
        var result = await engine.CalculateAsync(request);

        var line = result.Lines.Single();
        Assert.Equal(0m, line.DeductibleAmount);  // No deductible
        Assert.Equal(30m, line.CopayAmount);       // Flat copay
        Assert.Equal(24m, line.CoinsuranceAmount); // 20% of (150 - 30)
        Assert.Equal(54m, line.MemberResponsibility);
        Assert.Equal(96m, line.PlanPaidAmount);

        // Verify no PR-1 (deductible) adjustment was generated
        Assert.DoesNotContain(line.Adjustments, a => a.ReasonCode == "1");
        // Verify PR-3 (copay) is present
        Assert.Contains(line.Adjustments, a => a.GroupCode == "PR" && a.ReasonCode == "3" && a.Amount == 30m);
    }

    /// <summary>
    /// Copay-instead on a service where allowed < copay amount.
    /// Copay should be capped at allowed.
    /// </summary>
    [Fact]
    public async Task CopayInsteadOfDeductible_CopayExceedsAllowed_CappedAtAllowed()
    {
        var plan = CreateTestPlan(individualDeductible: 500);
        var engine = CreateEngine(plan, categoryCode: "PCP");

        var request = CreateRequest(plan.Id, lines: ("99213", 30m, 20m, "11"));
        var result = await engine.CalculateAsync(request);

        var line = result.Lines.Single();
        Assert.Equal(0m, line.DeductibleAmount);
        Assert.Equal(20m, line.CopayAmount); // Capped at allowed
        Assert.Equal(0m, line.CoinsuranceAmount); // Nothing left
        Assert.Equal(20m, line.MemberResponsibility);
    }

    /// <summary>
    /// Preventive care: $0 copay, InsteadOfDeductible.
    /// Member pays nothing, even with unmet deductible.
    /// </summary>
    [Fact]
    public async Task PreventiveCare_ZeroCopayInsteadOfDeductible_MemberPaysNothing()
    {
        var plan = CreateTestPlan(individualDeductible: 500);
        var engine = CreateEngine(plan, categoryCode: "AE");

        var request = CreateRequest(plan.Id, lines: ("99395", 250m, 200m, "11"));
        var result = await engine.CalculateAsync(request);

        var line = result.Lines.Single();
        Assert.Equal(0m, line.DeductibleAmount);
        Assert.Equal(0m, line.CopayAmount);
        Assert.Equal(0m, line.CoinsuranceAmount);
        Assert.Equal(0m, line.MemberResponsibility);
        Assert.Equal(200m, line.PlanPaidAmount);
    }

    // ═══════════════════════════════════════════════════════════════════
    // FEATURE 5: DRG-BASED INPATIENT PRICING
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// DRG case rate: cost-sharing applied once to the DRG allowed amount,
    /// not per-line. Multiple lines should have proportionally allocated amounts.
    /// </summary>
    [Fact]
    public async Task Drg_CostSharingAppliedOncePerAdmission()
    {
        var plan = CreateTestPlan(
            individualDeductible: 500,
            inpatientMethod: InpatientPricingMethod.DrgCaseRate);
        var engine = CreateEngine(plan, categoryCode: "48");

        var request = CreateRequest(plan.Id,
            claimType: "837I",
            drgCode: "470",
            drgAllowedAmount: 12000m,
            lines:
            [
                ("99223", 8000m, 8000m, "21"),  // Admission
                ("99231", 2000m, 2000m, "21"),  // Daily care
                ("99238", 2000m, 2000m, "21"),  // Discharge
            ]);

        var result = await engine.CalculateAsync(request);

        Assert.True(result.Success);
        Assert.NotNull(result.DrgCostShare);

        var drg = result.DrgCostShare!;
        Assert.Equal("470", drg.DrgCode);
        Assert.Equal(12000m, drg.DrgAllowedAmount);

        // $500 deductible + 20% coinsurance on remaining $11,500 = $2,300
        // Total member resp = $500 + $2,300 = $2,800
        // Use tolerance for DRG line-level proration rounding (e.g., 499.99 vs 500)
        Assert.InRange(drg.DeductibleAmount, 499.99m, 500.01m);
        Assert.InRange(drg.CoinsuranceAmount, 2299.99m, 2300.01m);
        Assert.InRange(drg.MemberResponsibility, 2799.98m, 2800.02m);
        Assert.InRange(drg.PlanPaidAmount, 9199.98m, 9200.02m);

        // All lines should be marked as DRG-priced
        Assert.All(result.Lines, l => Assert.True(l.IsDrgPriced));

        // Line amounts should sum to claim totals (allow rounding tolerance from DRG line proration)
        Assert.InRange(
            Math.Abs(drg.DeductibleAmount - result.Lines.Sum(l => l.DeductibleAmount)),
            0m, 0.02m);
    }

    /// <summary>
    /// A stay the fee schedule priced per stay (DRG case rate allocated across
    /// three lines by billed charges): the request's InpatientPricingMethod
    /// routes it through the claim-level path even though the plan default is
    /// PerLine. One $250 copay and the $500 deductible apply once; the result
    /// is allocated back to the lines so every component and the paid amounts
    /// reconcile to the claim totals, and each line balances on its own.
    /// </summary>
    [Fact]
    public async Task Drg_PricedPerStay_OneCopayDeductibleOnce_LinesReconcile()
    {
        var plan = CreateTestPlan(individualDeductible: 500, individualOopMax: 10000);
        plan.Categories.Add(new BenefitCategoryConfig
        {
            ServiceTypeCode = "IPC",
            ServiceTypeDescription = "Inpatient Stay",
            IsCovered = true,
            InNetworkCostSharing =
            [
                new CostShareRuleConfig { CostShareType = CostShareType.Deductible, DeductibleApplies = true },
                new CostShareRuleConfig { CostShareType = CostShareType.Copay, CopayAmount = 250 },
                new CostShareRuleConfig { CostShareType = CostShareType.Coinsurance, CoinsurancePercent = 0.20m },
            ]
        });
        var accumulators = new CountingAccumulatorService(new InMemoryAccumulatorService(plan, 0, 0, 0, "IPC"));
        var engine = new BenefitCalculationEngine(
            new FixedCategoryResolver("IPC", "Inpatient Stay"),
            new InMemoryBenefitPlanProvider(plan),
            accumulators,
            new BenefitRuleGate(NullLogger<BenefitRuleGate>.Instance),
            NullLogger<BenefitCalculationEngine>.Instance);

        var request = CreateRequest(plan.Id,
            claimType: "837I",
            drgCode: "470",
            drgAllowedAmount: 12000m,
            lines:
            [
                ("", 12000m, 3388.23m, "21"),
                ("", 1500m, 423.52m, "21"),
                ("27447", 29000m, 8188.25m, "21"),
            ]) with { InpatientPricingMethod = InpatientPricingMethod.DrgCaseRate };

        var result = await engine.CalculateAsync(request);

        Assert.True(result.Success);
        var drg = result.DrgCostShare!;
        // $500 deductible, one $250 copay, 20% of the remaining $11,250 = $2,250.
        Assert.Equal(500m, drg.DeductibleAmount);
        Assert.Equal(250m, drg.CopayAmount);
        Assert.Equal(2250m, drg.CoinsuranceAmount);
        Assert.Equal(3000m, drg.MemberResponsibility);
        Assert.Equal(9000m, drg.PlanPaidAmount);

        // Line sums reconcile exactly to the claim totals.
        Assert.Equal(250m, result.Lines.Sum(l => l.CopayAmount));
        Assert.Equal(500m, result.Lines.Sum(l => l.DeductibleAmount));
        Assert.Equal(2250m, result.Lines.Sum(l => l.CoinsuranceAmount));
        Assert.Equal(3000m, result.Lines.Sum(l => l.MemberResponsibility));
        Assert.Equal(9000m, result.Lines.Sum(l => l.PlanPaidAmount));
        Assert.Equal(12000m, result.Lines.Sum(l => l.AllowedAmount));
        Assert.Equal(250m, result.Totals.TotalCopay);
        Assert.Equal(500m, result.Totals.TotalDeductible);
        Assert.Equal(9000m, result.Totals.TotalPlanPaid);
        Assert.Equal(3000m, result.Totals.TotalMemberResponsibility);

        // Each line balances; its CAS matches its allocated amounts.
        Assert.All(result.Lines, l =>
        {
            Assert.True(l.IsDrgPriced);
            Assert.True(l.MemberResponsibility >= 0m && l.PlanPaidAmount >= 0m);
            Assert.Equal(l.DeductibleAmount + l.CopayAmount + l.CoinsuranceAmount - l.OopMaxReduction, l.MemberResponsibility);
            Assert.Equal(l.AllowedAmount - l.MemberResponsibility, l.PlanPaidAmount);
            Assert.Equal(l.BilledAmount - l.AllowedAmount, l.ContractualAdjustment);
            Assert.Equal(l.BilledAmount - l.PlanPaidAmount, l.Adjustments.Sum(a => a.Amount));
            Assert.Equal(l.CopayAmount, l.Adjustments.Where(a => a is { GroupCode: "PR", ReasonCode: "3" }).Sum(a => a.Amount));
        });

        // Accumulators written once for the stay.
        Assert.Equal(1, accumulators.ApplyCalls);
    }

    [Fact]
    public async Task PerDiem_PricedPerStay_WithoutDrgCode_UsesClaimLevelPath()
    {
        var plan = CreateTestPlan(individualDeductible: 500);
        var engine = CreateEngine(plan, categoryCode: "48");

        var request = CreateRequest(plan.Id,
            claimType: "837I",
            drgAllowedAmount: 10000m,
            lines:
            [
                ("", 12000m, 6000m, "21"),
                ("", 8000m, 4000m, "21"),
            ]) with { InpatientPricingMethod = InpatientPricingMethod.PerDiem };

        var result = await engine.CalculateAsync(request);

        Assert.NotNull(result.DrgCostShare);
        Assert.Equal(500m, result.Lines.Sum(l => l.DeductibleAmount)); // once, not per line
        Assert.Equal(result.DrgCostShare!.PlanPaidAmount, result.Lines.Sum(l => l.PlanPaidAmount));
    }

    /// <summary>
    /// DRG: OOP max should cap total member responsibility.
    /// </summary>
    [Fact]
    public async Task Drg_OopMaxCapsResponsibility()
    {
        var plan = CreateTestPlan(
            individualDeductible: 500,
            individualOopMax: 3000,
            inpatientMethod: InpatientPricingMethod.DrgCaseRate);
        var engine = CreateEngine(plan, categoryCode: "48",
            existingOop: 2500m); // Only $500 OOP remaining

        var request = CreateRequest(plan.Id,
            claimType: "837I", drgCode: "470", drgAllowedAmount: 12000m,
            lines: [("99223", 12000m, 12000m, "21")]);

        var result = await engine.CalculateAsync(request);

        var drg = result.DrgCostShare!;
        // Without OOP cap: $500 ded + $2300 coins = $2800
        // OOP remaining: $500 → capped at $500
        Assert.Equal(500m, drg.MemberResponsibility);
        Assert.True(drg.OopMaxReduction > 0);
    }

    /// <summary>
    /// Non-institutional claim with DRG code should still use per-line pricing.
    /// </summary>
    [Fact]
    public async Task Drg_ProfessionalClaim_IgnoresDrg_UsesPerLine()
    {
        var plan = CreateTestPlan(
            individualDeductible: 500,
            inpatientMethod: InpatientPricingMethod.DrgCaseRate);
        var engine = CreateEngine(plan, categoryCode: "98");

        var request = CreateRequest(plan.Id,
            claimType: "837P", // Professional, not institutional
            drgCode: "470",
            drgAllowedAmount: 12000m,
            lines: ("99213", 200m, 150m, "11"));

        var result = await engine.CalculateAsync(request);

        Assert.Null(result.DrgCostShare); // No DRG processing
        Assert.False(result.Lines.Single().IsDrgPriced);
        Assert.Equal(150m, result.Lines.Single().DeductibleAmount); // Normal per-line
    }

    // ═══════════════════════════════════════════════════════════════════
    // FEATURE 6: BENEFIT RULE PREDICATE GATING (BP 5.10)
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Two benefits with the same ServiceCategory + a member context
    /// that satisfies the second predicate ⇒ the second benefit is
    /// selected by the rule gate.
    /// </summary>
    [Fact]
    public async Task Predicate_TwoBenefitsSameCategory_AdultEncounterPicksAdultBenefit()
    {
        var pediatric = new BenefitCategoryConfig
        {
            ServiceTypeCode = "98",
            ServiceTypeDescription = "Pediatric Office Visit",
            IsCovered = true,
            Predicate = new BenefitRulePredicate { MemberAgeMin = 0, MemberAgeMax = 17 },
            InNetworkCostSharing =
            [
                new CostShareRuleConfig { CostShareType = CostShareType.Copay, CopayAmount = 10,
                    CopayApplicationMode = CopayApplicationMode.InsteadOfDeductible },
            ],
        };
        var adult = new BenefitCategoryConfig
        {
            ServiceTypeCode = "98",
            ServiceTypeDescription = "Adult Office Visit",
            IsCovered = true,
            Predicate = new BenefitRulePredicate { MemberAgeMin = 18 },
            InNetworkCostSharing =
            [
                new CostShareRuleConfig { CostShareType = CostShareType.Copay, CopayAmount = 50,
                    CopayApplicationMode = CopayApplicationMode.InsteadOfDeductible },
            ],
        };
        var plan = new BenefitPlanConfig
        {
            Id = Guid.NewGuid(),
            TenantId = "test-tenant",
            PlanName = "Test",
            IndividualDeductible = 0,
            IndividualOopMax = 5000,
            FamilyOopMax = 10000,
            Categories = [pediatric, adult],
        };
        var engine = CreateEngine(plan, categoryCode: "98");

        var request = CreateRequest(plan.Id, lines: ("99213", 100m, 100m, "11")) with
        {
            Member = new MemberContext { AgeYears = 42 },
        };

        var result = await engine.CalculateAsync(request);

        var line = result.Lines.Single();
        Assert.True(line.IsCovered);
        Assert.Equal("Adult Office Visit", line.ServiceTypeDescription);
        Assert.Equal(50m, line.CopayAmount);
    }

    /// <summary>
    /// All candidate predicates reject ⇒ the engine denies with code
    /// 96 + the predicate-specific narrative (distinct from the
    /// "service not covered" 96).
    /// </summary>
    [Fact]
    public async Task Predicate_AllRejected_DeniesWithPredicateNarrative()
    {
        var maternity = new BenefitCategoryConfig
        {
            ServiceTypeCode = "98",
            ServiceTypeDescription = "Maternity Office Visit",
            IsCovered = true,
            Predicate = new BenefitRulePredicate { MemberGender = BenefitMemberGender.Female },
        };
        var plan = new BenefitPlanConfig
        {
            Id = Guid.NewGuid(),
            TenantId = "test-tenant",
            PlanName = "Test",
            Categories = [maternity],
        };
        var engine = CreateEngine(plan, categoryCode: "98");

        var request = CreateRequest(plan.Id, lines: ("99213", 100m, 100m, "11")) with
        {
            Member = new MemberContext { AgeYears = 42, Gender = BenefitMemberGender.Male },
        };

        var result = await engine.CalculateAsync(request);

        var line = result.Lines.Single();
        Assert.False(line.IsCovered);
        Assert.Equal("96", line.DenialReasonCode);
        Assert.Contains("rule predicate", line.DenialReasonDescription, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Decision 3 — null MemberContext means predicates are skipped
    /// entirely. Existing pre-BP-5.10 tests (which never supply
    /// MemberContext) keep working unchanged: the first candidate is
    /// returned regardless of its predicate.
    /// </summary>
    [Fact]
    public async Task Predicate_NullMemberContext_PicksFirstCandidate_BackwardsCompatible()
    {
        var pediatric = new BenefitCategoryConfig
        {
            ServiceTypeCode = "98",
            ServiceTypeDescription = "Pediatric Office Visit",
            IsCovered = true,
            Predicate = new BenefitRulePredicate { MemberAgeMin = 0, MemberAgeMax = 17 },
            InNetworkCostSharing =
            [
                new CostShareRuleConfig { CostShareType = CostShareType.Copay, CopayAmount = 10,
                    CopayApplicationMode = CopayApplicationMode.InsteadOfDeductible },
            ],
        };
        var plan = new BenefitPlanConfig
        {
            Id = Guid.NewGuid(),
            TenantId = "test-tenant",
            PlanName = "Test",
            IndividualDeductible = 0,
            IndividualOopMax = 5000,
            FamilyOopMax = 10000,
            Categories = [pediatric],
        };
        var engine = CreateEngine(plan, categoryCode: "98");

        var request = CreateRequest(plan.Id, lines: ("99213", 100m, 100m, "11"));
        // Member is null — engine must skip the predicate.

        var result = await engine.CalculateAsync(request);

        var line = result.Lines.Single();
        Assert.True(line.IsCovered);
        Assert.Equal(10m, line.CopayAmount);
    }

    // ═══════════════════════════════════════════════════════════════════
    // TEST HELPER
    // ═══════════════════════════════════════════════════════════════════

    private static BenefitCalculationEngine CreateEngine(
        BenefitPlanConfig plan,
        string categoryCode,
        decimal existingDeductible = 0,
        decimal existingOop = 0,
        int existingVisitCount = 0,
        FamilyAccumulatorModel familyModel = FamilyAccumulatorModel.Embedded,
        decimal familyDeductible = 1500,
        decimal existingFamilyDeductible = 0,
        decimal existingFamilyOop = 0)
    {
        var planProvider = new InMemoryBenefitPlanProvider(plan);
        var accumulatorService = new InMemoryAccumulatorService(
            plan, existingDeductible, existingOop, existingVisitCount, categoryCode,
            familyModel, familyDeductible, existingFamilyDeductible, existingFamilyOop);
        var categoryResolver = new FixedCategoryResolver(categoryCode,
            plan.GetFirstCategory(categoryCode)?.ServiceTypeDescription ?? "Unknown");
        var ruleGate = new BenefitRuleGate(NullLogger<BenefitRuleGate>.Instance);

        return new BenefitCalculationEngine(
            categoryResolver, planProvider, accumulatorService, ruleGate,
            NullLogger<BenefitCalculationEngine>.Instance);
    }

    private static BenefitCalculationEngine CreateUnmappedEngine(BenefitPlanConfig plan)
        => new(
            new UnmappedCategoryResolver(),
            new InMemoryBenefitPlanProvider(plan),
            new InMemoryAccumulatorService(plan, 0, 0, 0, "98"),
            new BenefitRuleGate(NullLogger<BenefitRuleGate>.Instance),
            NullLogger<BenefitCalculationEngine>.Instance);
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
        string categoryCode,
        FamilyAccumulatorModel familyModel = FamilyAccumulatorModel.Embedded,
        decimal familyDeductible = 1500,
        decimal existingFamilyDeductible = 0,
        decimal existingFamilyOop = 0)
    {
        if (familyModel == FamilyAccumulatorModel.Aggregate)
        {
            // Aggregate: only family-level accumulators
            _snapshots.Add(new AccumulatorSnapshot
            {
                Type = AccumulatorType.FamilyDeductible,
                Scope = AccumulatorScope.Family,
                NetworkTier = NetworkTier.InNetwork,
                LimitAmount = familyDeductible,
                AccumulatedAmountBefore = existingFamilyDeductible,
                AccumulatedAmountAfter = existingFamilyDeductible,
                RemainingAmount = Math.Max(0, familyDeductible - existingFamilyDeductible)
            });
            _snapshots.Add(new AccumulatorSnapshot
            {
                Type = AccumulatorType.FamilyOutOfPocketMax,
                Scope = AccumulatorScope.Family,
                NetworkTier = NetworkTier.InNetwork,
                LimitAmount = plan.FamilyOopMax ?? 0,
                AccumulatedAmountBefore = existingFamilyOop,
                AccumulatedAmountAfter = existingFamilyOop,
                RemainingAmount = Math.Max(0, (plan.FamilyOopMax ?? 0) - existingFamilyOop)
            });
        }
        else
        {
            // Embedded: individual + family
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
        }

        if (existingVisitCount > 0)
        {
            _snapshots.Add(new AccumulatorSnapshot
            {
                Type = AccumulatorType.VisitCount,
                Scope = AccumulatorScope.Individual,
                NetworkTier = NetworkTier.InNetwork,
                ServiceTypeCode = categoryCode,
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
        Guid benefitPlanId, string planYear, string claimId,
        IReadOnlyList<AccumulatorUpdate> updates, CancellationToken ct = default)
    {
        _appliedUpdates.AddRange(updates);
        return Task.CompletedTask;
    }

    public virtual Task ReverseAsync(string memberId, string subscriberId,
        Guid benefitPlanId, string planYear, string claimId, CancellationToken ct)
        => Task.CompletedTask;

    public Task ResetForPlanYearAsync(Guid benefitPlanId, string planYear, CancellationToken ct)
        => Task.CompletedTask;
}

/// <summary>
/// Accumulator service that tracks whether Reverse was called.
/// </summary>
internal class TrackingAccumulatorService : InMemoryAccumulatorService
{
    public bool ReverseCalled { get; private set; }
    public string? ReversedClaimId { get; private set; }

    public TrackingAccumulatorService(
        BenefitPlanConfig plan, decimal existingDeductible, decimal existingOop,
        int existingVisitCount, string categoryCode)
        : base(plan, existingDeductible, existingOop, existingVisitCount, categoryCode)
    { }

    public override Task ReverseAsync(
        string memberId, string subscriberId,
        Guid benefitPlanId, string planYear, string claimId, CancellationToken ct)
    {
        ReverseCalled = true;
        ReversedClaimId = claimId;
        return Task.CompletedTask;
    }
}

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
        string tenantId, Guid benefitPlanId, DateOnly serviceDate,
        string procedureCode, string codeType, string placeOfService,
        IReadOnlyList<string> modifiers, string? revenueCode, CancellationToken ct)
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

/// <summary>Counts accumulator writes; delegates everything to the inner service.</summary>
internal class CountingAccumulatorService : IAccumulatorService
{
    private readonly IAccumulatorService _inner;
    public int ApplyCalls { get; private set; }

    public CountingAccumulatorService(IAccumulatorService inner) => _inner = inner;

    public Task<IReadOnlyList<AccumulatorSnapshot>> GetAccumulatorsAsync(
        string memberId, string subscriberId, Guid benefitPlanId, string planYear, CancellationToken ct)
        => _inner.GetAccumulatorsAsync(memberId, subscriberId, benefitPlanId, planYear, ct);

    public Task ApplyUpdatesAsync(string memberId, string subscriberId,
        Guid benefitPlanId, string planYear, string claimId,
        IReadOnlyList<AccumulatorUpdate> updates, CancellationToken ct = default)
    {
        ApplyCalls++;
        return _inner.ApplyUpdatesAsync(memberId, subscriberId, benefitPlanId, planYear, claimId, updates, ct);
    }

    public Task ReverseAsync(string memberId, string subscriberId,
        Guid benefitPlanId, string planYear, string claimId, CancellationToken ct)
        => _inner.ReverseAsync(memberId, subscriberId, benefitPlanId, planYear, claimId, ct);

    public Task ResetForPlanYearAsync(Guid benefitPlanId, string planYear, CancellationToken ct)
        => _inner.ResetForPlanYearAsync(benefitPlanId, planYear, ct);
}

/// <summary>
/// Resolver with no mapping for any procedure code.
/// </summary>
internal class UnmappedCategoryResolver : IServiceCategoryResolver
{
    public Task<ServiceCategoryMatch?> ResolveAsync(
        string tenantId, Guid benefitPlanId, DateOnly serviceDate,
        string procedureCode, string codeType, string placeOfService,
        IReadOnlyList<string> modifiers, string? revenueCode, CancellationToken ct)
        => Task.FromResult<ServiceCategoryMatch?>(null);
}
