using CloudHealthOffice.BenefitEngine.Domain;
using CloudHealthOffice.BenefitEngine.Models;
using CloudHealthOffice.BenefitEngine.Services;
using CloudHealthOffice.CobEngine.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using CobInfo = CloudHealthOffice.BenefitEngine.Models.CobInfo;
using Xunit;

namespace CloudHealthOffice.BenefitEngine.Tests;

public partial class BenefitCalculationEngineTests
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
        // 20% coinsurance ($600) is reduced to the $20 the member owes.
        Assert.Equal(580m, line.OopMaxReduction);
        Assert.Equal(20m, line.CoinsuranceAmount);
        Assert.Equal(
            new[] { ("CO", "45", 2000m), ("PR", "2", 20m) },
            line.Adjustments.Select(a => (a.GroupCode, a.ReasonCode, a.Amount)));
        AssertCasInvariants(line);
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
        // reduced to $10; coinsurance ($24) is excluded and owed in full.
        var plan = WithOopApplies(CreateTestPlan(individualDeductible: 500, individualOopMax: 3000),
            "98", false, CostShareType.Coinsurance);
        var engine = CreateEngine(plan, categoryCode: "98", existingDeductible: 500m, existingOop: 2990m);
        var request = CreateRequest(plan.Id, lines: ("99213", 200m, 150m, "11"));

        var result = await engine.CalculateAsync(request);

        var line = result.Lines.Single();
        Assert.Equal(10m, line.CopayAmount);
        Assert.Equal(24m, line.CoinsuranceAmount);
        Assert.Equal(20m, line.OopMaxReduction);
        AssertCasInvariants(line);
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
            Assert.Equal(l.DeductibleAmount + l.CopayAmount + l.CoinsuranceAmount, l.MemberResponsibility);
            Assert.Equal(l.AllowedAmount - l.MemberResponsibility, l.PlanPaidAmount);
            Assert.Equal(l.BilledAmount - l.AllowedAmount, l.ContractualAdjustment);
            Assert.Equal(l.BilledAmount - l.PlanPaidAmount, l.Adjustments.Sum(a => a.Amount));
            Assert.Equal(l.CopayAmount, l.Adjustments.Where(a => a is { GroupCode: "PR", ReasonCode: "3" }).Sum(a => a.Amount));
        });

        // Every component counts toward the OOP max here: the OOP-applied
        // portion allocates with member responsibility and sums to it.
        Assert.Equal(3000m, result.Lines.Sum(l => l.OopAppliedAmount));
        Assert.Equal(3000m, result.Totals.TotalOopApplied);
        Assert.All(result.Lines, l => Assert.Equal(l.MemberResponsibility, l.OopAppliedAmount));

        // Accumulators written once for the stay.
        Assert.Equal(1, accumulators.ApplyCalls);
    }

    /// <summary>
    /// A per-stay rate above total billed (no lesser-of-billed provision)
    /// allocates every line more than it billed; the line-level remittance
    /// cannot carry the negative contractual adjustment, so the engine pends
    /// for pricing review before computing cost share or writing accumulators.
    /// </summary>
    [Fact]
    public async Task Drg_PricedPerStay_AllowedExceedsBilled_PendsBeforeCostShareAndAccumulators()
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
                ("", 2000m, 8000m, "21"),
                ("", 1000m, 4000m, "21"),
            ]) with { InpatientPricingMethod = InpatientPricingMethod.DrgCaseRate };

        var result = await engine.CalculateAsync(request);

        Assert.False(result.Success);
        Assert.True(result.RequiresReview);
        Assert.Equal(BenefitCalculationEngine.AllowedExceedsBilledPendCode, result.PendReasonCode);
        Assert.Contains("line 1 allowed 8000.00 > billed 2000.00", result.PendReason);
        Assert.Null(result.DenialReasonCode);
        Assert.Empty(result.Lines);
        Assert.Null(result.DrgCostShare);
        Assert.Equal(0, accumulators.ApplyCalls);
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
        // OOP remaining: $500 → capped at $500: coinsurance forgiven entirely,
        // the deductible stands.
        Assert.Equal(500m, drg.MemberResponsibility);
        Assert.Equal(2300m, drg.OopMaxReduction);
        Assert.Equal(500m, drg.DeductibleAmount);
        Assert.Equal(0m, drg.CoinsuranceAmount);
        Assert.Equal(
            new[] { ("PR", "1", 500m) },
            drg.Adjustments.Select(a => (a.GroupCode, a.ReasonCode, a.Amount)));
        Assert.All(result.Lines, AssertCasInvariants);
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
    // OOP MAX — REDUCED PATIENT RESPONSIBILITY (no negative OA-23)
    // The cap forgives the cost share applied last first: coinsurance,
    // then copay, then deductible.
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Deductible met, $40 OOP left. Raw cost share: $30 copay + 20% × $120
    /// = $24 coinsurance = $54. The cap lands mid-coinsurance: PR-3 $30,
    /// PR-2 $10, plan pays $110.
    /// </summary>
    [Fact]
    public async Task OopMax_CapHitMidCoinsurance_ReducesCoinsuranceOnly()
    {
        var plan = CreateTestPlan(individualDeductible: 500, individualOopMax: 3000);
        var engine = CreateEngine(plan, categoryCode: "98", existingDeductible: 500m, existingOop: 2960m);
        var request = CreateRequest(plan.Id, lines: ("99213", 200m, 150m, "11"));

        var result = await engine.CalculateAsync(request);

        var line = result.Lines.Single();
        Assert.Equal(0m, line.DeductibleAmount);
        Assert.Equal(30m, line.CopayAmount);
        Assert.Equal(10m, line.CoinsuranceAmount);
        Assert.Equal(14m, line.OopMaxReduction);
        Assert.Equal(40m, line.MemberResponsibility);
        Assert.Equal(110m, line.PlanPaidAmount);
        Assert.Equal(
            new[] { ("CO", "45", 50m), ("PR", "3", 30m), ("PR", "2", 10m) },
            line.Adjustments.Select(a => (a.GroupCode, a.ReasonCode, a.Amount)));
        AssertCasInvariants(line);
        Assert.Equal(40m, Snapshot(result, AccumulatorType.IndividualOutOfPocketMax).AmountApplied);
        Assert.Equal(40m, line.OopAppliedAmount);
    }

    /// <summary>
    /// Deductible met, $10 OOP left: coinsurance ($24) is forgiven entirely
    /// and dropped; the copay is reduced from $30 to $10.
    /// </summary>
    [Fact]
    public async Task OopMax_CapHitMidCopay_DropsCoinsuranceReducesCopay()
    {
        var plan = CreateTestPlan(individualDeductible: 500, individualOopMax: 3000);
        var engine = CreateEngine(plan, categoryCode: "98", existingDeductible: 500m, existingOop: 2990m);
        var request = CreateRequest(plan.Id, lines: ("99213", 200m, 150m, "11"));

        var result = await engine.CalculateAsync(request);

        var line = result.Lines.Single();
        Assert.Equal(10m, line.CopayAmount);
        Assert.Equal(0m, line.CoinsuranceAmount);
        Assert.Equal(44m, line.OopMaxReduction);
        Assert.Equal(10m, line.MemberResponsibility);
        Assert.Equal(140m, line.PlanPaidAmount);
        Assert.Equal(
            new[] { ("CO", "45", 50m), ("PR", "3", 10m) },
            line.Adjustments.Select(a => (a.GroupCode, a.ReasonCode, a.Amount)));
        AssertCasInvariants(line);
    }

    /// <summary>
    /// OOP left ($300) is below the deductible still owed ($500) — e.g. OOP
    /// already accumulated through copays that do not count toward the
    /// deductible. Raw: $500 deductible + $30 copay + 20% × $470 = $94
    /// coinsurance = $624. Coinsurance and copay are forgiven, the deductible
    /// is reduced to $300, and the deductible accumulator records the $300
    /// actually charged, not $500.
    /// </summary>
    [Fact]
    public async Task OopMax_CapBelowDeductible_ReducesDeductibleAndDeductibleAccumulator()
    {
        var plan = CreateTestPlan(individualDeductible: 500, individualOopMax: 3000);
        var engine = CreateEngine(plan, categoryCode: "98", existingDeductible: 0m, existingOop: 2700m);
        var request = CreateRequest(plan.Id, lines: ("99214", 1200m, 1000m, "11"));

        var result = await engine.CalculateAsync(request);

        var line = result.Lines.Single();
        Assert.Equal(300m, line.DeductibleAmount);
        Assert.Equal(0m, line.CopayAmount);
        Assert.Equal(0m, line.CoinsuranceAmount);
        Assert.Equal(324m, line.OopMaxReduction);
        Assert.Equal(300m, line.MemberResponsibility);
        Assert.Equal(700m, line.PlanPaidAmount);
        Assert.Equal(
            new[] { ("CO", "45", 200m), ("PR", "1", 300m) },
            line.Adjustments.Select(a => (a.GroupCode, a.ReasonCode, a.Amount)));
        AssertCasInvariants(line);
        Assert.Equal(300m, Snapshot(result, AccumulatorType.IndividualDeductible).AmountApplied);
        Assert.Equal(300m, Snapshot(result, AccumulatorType.FamilyDeductible, AccumulatorScope.Family).AmountApplied);
        Assert.Equal(300m, Snapshot(result, AccumulatorType.IndividualOutOfPocketMax).AmountApplied);
    }

    /// <summary>
    /// The cap reached on a later line of the same claim: line 1 consumes
    /// OOP, line 2 is reduced. The deductible satisfied on line 1 keeps
    /// counting in full.
    /// </summary>
    [Fact]
    public async Task OopMax_CrossedOnSecondLine_EarlierLineUntouched()
    {
        var plan = CreateTestPlan(individualDeductible: 500, individualOopMax: 3000);
        var engine = CreateEngine(plan, categoryCode: "98", existingDeductible: 400m, existingOop: 2850m);
        var request = CreateRequest(plan.Id, lines:
        [
            ("99213", 200m, 150m, "11"),
            ("99213", 200m, 150m, "11"),
        ]);

        var result = await engine.CalculateAsync(request);

        // Line 1: deductible $100, copay $30, 20% × $20 = $4 → $134 (OOP left $150 → $16).
        // Line 2: copay $30 + coinsurance $24 = $54, capped at $16 → copay $16.
        var (l1, l2) = (result.Lines[0], result.Lines[1]);
        Assert.Equal((100m, 30m, 4m, 0m), (l1.DeductibleAmount, l1.CopayAmount, l1.CoinsuranceAmount, l1.OopMaxReduction));
        Assert.Equal((0m, 16m, 0m, 38m), (l2.DeductibleAmount, l2.CopayAmount, l2.CoinsuranceAmount, l2.OopMaxReduction));
        Assert.Equal(150m, result.Totals.TotalMemberResponsibility);
        Assert.Equal(150m, result.Totals.TotalDeductible + result.Totals.TotalCopay + result.Totals.TotalCoinsurance);
        Assert.All(result.Lines, AssertCasInvariants);
        Assert.Equal(100m, Snapshot(result, AccumulatorType.IndividualDeductible).AmountApplied);
        Assert.Equal(150m, Snapshot(result, AccumulatorType.IndividualOutOfPocketMax).AmountApplied);
    }

    /// <summary>
    /// DRG stay crossing the cap, three lines with uneven allowed amounts.
    /// Claim level: $500 deductible + 20% × $11,500 = $2,300 coinsurance;
    /// $1,500 OOP left → coinsurance reduced to $1,000. Per line the reduced
    /// amounts are split by allowed (truncated to the cent, remainder on the
    /// last line), each line balances, and no OA-23 is emitted.
    /// </summary>
    [Fact]
    public async Task Drg_StayCrossingOopMax_ReducedPrSplitAcrossLines()
    {
        var plan = CreateTestPlan(
            individualDeductible: 500,
            individualOopMax: 3000,
            inpatientMethod: InpatientPricingMethod.DrgCaseRate);
        var engine = CreateEngine(plan, categoryCode: "48", existingOop: 1500m);

        var request = CreateRequest(plan.Id,
            claimType: "837I", drgCode: "470", drgAllowedAmount: 12000m,
            lines:
            [
                ("99223", 12000m, 3388.23m, "21"),
                ("", 1500m, 423.52m, "21"),
                ("", 29000m, 8188.25m, "21"),
            ]);

        var result = await engine.CalculateAsync(request);

        Assert.True(result.Success);
        var drg = result.DrgCostShare!;
        Assert.Equal(500m, drg.DeductibleAmount);
        Assert.Equal(1000m, drg.CoinsuranceAmount);
        Assert.Equal(1300m, drg.OopMaxReduction);
        Assert.Equal(1500m, drg.MemberResponsibility);
        Assert.Equal(10500m, drg.PlanPaidAmount);
        Assert.DoesNotContain(drg.Adjustments, a => a.GroupCode == "OA");
        Assert.All(drg.Adjustments, a => Assert.True(a.Amount >= 0));
        Assert.Equal(drg.PlanPaidAmount,
            result.Lines.Sum(l => l.BilledAmount) - drg.Adjustments.Sum(a => a.Amount));

        // Truncated proportional split, remainder on the last line.
        Assert.Equal(new[] { 141.17m, 17.64m, 341.19m }, result.Lines.Select(l => l.DeductibleAmount));
        Assert.Equal(new[] { 282.35m, 35.29m, 682.36m }, result.Lines.Select(l => l.CoinsuranceAmount));
        Assert.Equal(1300m, result.Lines.Sum(l => l.OopMaxReduction));
        Assert.Equal(1300m, result.Totals.TotalOopMaxReduction);
        Assert.Equal(1500m, result.Totals.TotalMemberResponsibility);
        Assert.Equal(1500m, result.Totals.TotalOopApplied);
        Assert.Equal(10500m, result.Totals.TotalPlanPaid);
        Assert.All(result.Lines, l =>
        {
            AssertCasInvariants(l);
            Assert.Equal(l.DeductibleAmount + l.CopayAmount + l.CoinsuranceAmount, l.MemberResponsibility);
            Assert.DoesNotContain(l.Adjustments, a => a.GroupCode == "OA");
        });
        Assert.Equal(500m, Snapshot(result, AccumulatorType.IndividualDeductible).AmountApplied);
        Assert.Equal(1500m, Snapshot(result, AccumulatorType.IndividualOutOfPocketMax).AmountApplied);
    }

    /// <summary>
    /// DRG stay with OOP-excluded coinsurance: only the OOP-eligible
    /// deductible is reduced ($500 → the $200 left); the $2,300 coinsurance
    /// is owed in full and its per-line split is untouched by the cap.
    /// </summary>
    [Fact]
    public async Task Drg_OopExcludedCoinsurance_OnlyEligibleComponentReduced()
    {
        var plan = WithOopApplies(CreateTestPlan(
            individualDeductible: 500,
            individualOopMax: 3000,
            inpatientMethod: InpatientPricingMethod.DrgCaseRate), "48", false, CostShareType.Coinsurance);
        var engine = CreateEngine(plan, categoryCode: "48", existingOop: 2800m);

        var request = CreateRequest(plan.Id,
            claimType: "837I", drgCode: "470", drgAllowedAmount: 12000m,
            lines:
            [
                ("99223", 9000m, 4000m, "21"),
                ("", 20000m, 8000m, "21"),
            ]);

        var result = await engine.CalculateAsync(request);

        var drg = result.DrgCostShare!;
        Assert.Equal(200m, drg.DeductibleAmount);
        Assert.Equal(2300m, drg.CoinsuranceAmount);
        Assert.Equal(300m, drg.OopMaxReduction);
        Assert.Equal(2500m, drg.MemberResponsibility);
        Assert.Equal(2300m, result.Lines.Sum(l => l.CoinsuranceAmount));
        Assert.Equal(200m, result.Totals.TotalOopApplied);
        Assert.All(result.Lines, AssertCasInvariants);
        Assert.Equal(200m, Snapshot(result, AccumulatorType.IndividualDeductible).AmountApplied);
    }

    // ═══════════════════════════════════════════════════════════════════
    // COB — SECONDARY PAYER: PR = MEMBER LIABILITY, POSITIVE OA-23,
    // 835 BALANCING (charge − ΣCAS = paid)
    //
    // Convention: the member owes min(pre-COB cost share, allowed − primary
    // paid − our payment). PR-1/2/3 are reduced to that (coinsurance, then
    // copay, then deductible); OA-23 = allowed − member − paid; the
    // accumulators record the reduced PR amounts.
    // ═══════════════════════════════════════════════════════════════════

    private static BenefitResolutionRequest AsSecondary(
        BenefitResolutionRequest request, bool complementary, params (int line, decimal paid)[] primary)
        => request with
        {
            Cob = new CobInfo
            {
                PayerSequence = 2,
                UseComplementaryModel = complementary,
                PrimaryPayerId = "PRIMARY-1",
                PrimaryPayerPaymentByLine = primary.ToDictionary(p => p.line, p => p.paid),
            }
        };

    private static void AssertLineBalances(LineBenefitResult l) =>
        Assert.Equal(l.PlanPaidAmount, l.BilledAmount - l.Adjustments.Sum(a => a.Amount));

    /// <summary>
    /// 835 line invariants: charge − ΣCAS = paid, no negative CAS amount,
    /// no zero-amount PR entry, the PR entries equal the line's
    /// deductible / copay / coinsurance, and ΣPR = member responsibility.
    /// </summary>
    private static void AssertCasInvariants(LineBenefitResult l)
    {
        AssertLineBalances(l);
        Assert.All(l.Adjustments, a => Assert.True(a.Amount >= 0,
            $"line {l.LineNumber} {a.GroupCode}-{a.ReasonCode} is negative ({a.Amount})"));
        Assert.DoesNotContain(l.Adjustments, a => a.GroupCode == "PR" && a.Amount == 0);
        decimal Pr(string carc) => l.Adjustments
            .Where(a => a.GroupCode == "PR" && a.ReasonCode == carc).Sum(a => a.Amount);
        Assert.Equal(l.DeductibleAmount, Pr("1"));
        Assert.Equal(l.CoinsuranceAmount, Pr("2"));
        Assert.Equal(l.CopayAmount, Pr("3"));
        Assert.Equal(l.MemberResponsibility, l.Adjustments.Where(a => a.GroupCode == "PR").Sum(a => a.Amount));
    }

    private static decimal Oa23(IEnumerable<AdjustmentReason> adjustments) =>
        adjustments.Where(a => a is { GroupCode: "OA", ReasonCode: "23" }).Sum(a => a.Amount);

    /// <summary>
    /// Deductible met: pre-COB the plan pays $96 on $150 allowed ($30 copay,
    /// $24 coinsurance). Complementary pays min($96, $150 allowed − primary):
    /// all plans together never exceed the allowable expense (NAIC MDL-120
    /// §7). The member owes what is left of the $150 allowed after both payers
    /// (never more than $54), the PR entries shrink to it (coinsurance
    /// first), and OA-23 = allowed − member − paid.
    /// </summary>
    [Theory]
    [InlineData(0, 96, 30, 24, 0)]     // no primary payment: COB is a no-op
    [InlineData(40, 96, 14, 0, 40)]    // member owes 150 − 40 − 96 = 14 (copay)
    [InlineData(120, 30, 0, 0, 120)]   // payment capped at allowed 150 − 120; member owes 0
    public async Task Cob_Complementary_Line_PrIsMemberLiability_Oa23Balances(
        decimal primaryPaid, decimal expectedPaid, decimal expectedCopay,
        decimal expectedCoinsurance, decimal expectedOa23)
    {
        var plan = CreateTestPlan(individualDeductible: 500);
        var engine = CreateEngine(plan, categoryCode: "98", existingDeductible: 500m);
        var request = AsSecondary(
            CreateRequest(plan.Id, lines: ("99213", 200m, 150m, "11")),
            complementary: true, (1, primaryPaid));

        var result = await engine.CalculateAsync(request);

        var line = result.Lines.Single();
        Assert.Equal(expectedPaid, line.PlanPaidAmount);
        Assert.Equal(expectedCopay, line.CopayAmount);
        Assert.Equal(expectedCoinsurance, line.CoinsuranceAmount);
        Assert.Equal(expectedCopay + expectedCoinsurance, line.MemberResponsibility);
        Assert.Equal(expectedOa23, Oa23(line.Adjustments));
        AssertCasInvariants(line);
        // Accumulators record what the member actually owes.
        Assert.Equal(line.MemberResponsibility, line.OopAppliedAmount);
        Assert.Equal(line.MemberResponsibility,
            Snapshot(result, AccumulatorType.IndividualOutOfPocketMax).AmountApplied);
    }

    /// <summary>
    /// Non-duplication: max benefit = allowed − member share = $96. The
    /// secondary pays max benefit − primary; the member keeps the full $54
    /// cost share unless the primary paid more than $96, then owes only
    /// what is left of the $150 allowed. OA-23 equals the primary payment
    /// here (it covers our savings plus any cost share the primary paid).
    /// </summary>
    [Theory]
    [InlineData(60, 36, 30, 24, 60)]
    [InlineData(120, 0, 30, 0, 120)]   // member owes 150 − 120 = 30
    [InlineData(200, 0, 0, 0, 150)]    // primary paid more than allowed
    public async Task Cob_NonDuplication_Line_PrIsMemberLiability_Oa23Balances(
        decimal primaryPaid, decimal expectedPaid, decimal expectedCopay,
        decimal expectedCoinsurance, decimal expectedOa23)
    {
        var plan = CreateTestPlan(individualDeductible: 500);
        var engine = CreateEngine(plan, categoryCode: "98", existingDeductible: 500m);
        var request = AsSecondary(
            CreateRequest(plan.Id, lines: ("99213", 200m, 150m, "11")),
            complementary: false, (1, primaryPaid));

        var result = await engine.CalculateAsync(request);

        var line = result.Lines.Single();
        Assert.Equal(expectedPaid, line.PlanPaidAmount);
        Assert.Equal(expectedCopay, line.CopayAmount);
        Assert.Equal(expectedCoinsurance, line.CoinsuranceAmount);
        Assert.Equal(expectedOa23, Oa23(line.Adjustments));
        AssertCasInvariants(line);
        Assert.Equal(line.MemberResponsibility,
            Snapshot(result, AccumulatorType.IndividualOutOfPocketMax).AmountApplied);
    }

    /// <summary>
    /// Deductible not met: the $150 allowed all goes to the deductible and
    /// the plan pays nothing pre-COB. The primary paid $100, so the member
    /// owes $50 — PR-1 $50, OA-23 $100, the same 835 under either deductible
    /// setting. The deductible accumulator records the full $150 the plan
    /// would have applied with no other coverage under NAIC full credit (the
    /// default; MDL-120 §7), or only the member's $50 under MemberPaidOnly.
    /// The OOP accumulator records the member's $50 either way.
    /// </summary>
    [Theory]
    [InlineData(true, CobDeductibleCredit.NaicFullCredit, 150)]
    [InlineData(false, CobDeductibleCredit.NaicFullCredit, 150)]
    [InlineData(true, CobDeductibleCredit.MemberPaidOnly, 50)]
    [InlineData(false, CobDeductibleCredit.MemberPaidOnly, 50)]
    public async Task Cob_DeductibleReducedToMemberLiability_AccumulatorFollowsCreditSetting(
        bool complementary, CobDeductibleCredit credit, decimal expectedDeductibleCredit)
    {
        var plan = CreateTestPlan(individualDeductible: 500) with { CobDeductibleCredit = credit };
        var engine = CreateEngine(plan, categoryCode: "98");
        var request = AsSecondary(
            CreateRequest(plan.Id, lines: ("99213", 200m, 150m, "11")),
            complementary, (1, 100m));

        var result = await engine.CalculateAsync(request);

        var line = result.Lines.Single();
        Assert.Equal(0m, line.PlanPaidAmount);
        Assert.Equal(
            new[] { ("CO", "45", 50m), ("PR", "1", 50m), ("OA", "23", 100m) },
            line.Adjustments.Select(a => (a.GroupCode, a.ReasonCode, a.Amount)));
        Assert.Equal(50m, line.MemberResponsibility);
        AssertCasInvariants(line);
        Assert.Equal(expectedDeductibleCredit, Snapshot(result, AccumulatorType.IndividualDeductible).AmountApplied);
        Assert.Equal(expectedDeductibleCredit, line.DeductibleCreditedAmount);
        Assert.Equal(expectedDeductibleCredit, result.Totals.TotalDeductibleCredited);
        Assert.Equal(50m, Snapshot(result, AccumulatorType.IndividualOutOfPocketMax).AmountApplied);
        Assert.Equal(50m, result.Totals.TotalDeductible);
        Assert.Equal(50m, result.Totals.TotalMemberResponsibility);
    }

    /// <summary>
    /// OOP max and COB on the same line. Pre-COB the $600 coinsurance is
    /// reduced to the $20 left under the OOP max and the plan would pay
    /// $2,980. Complementary pays the $500 of the $3,000 allowed the
    /// primary's $2,500 left (never more than allowed − prior paid, NAIC
    /// MDL-120 §7); nothing is left for the member, so PR-2 is dropped,
    /// OA-23 = $2,500 and the OOP accumulator records $0.
    /// Non-duplication pays $2,980 − $2,500 = $480; the member still owes
    /// the $20, OA-23 = $2,500.
    /// </summary>
    [Theory]
    [InlineData(true, 500, 0, 2500)]
    [InlineData(false, 480, 20, 2500)]
    public async Task Cob_WithOopMaxReduction_Line_ReducedPrAndPositiveCobOa23(
        bool complementary, decimal expectedPaid, decimal expectedCoinsurance, decimal expectedOa23)
    {
        var plan = CreateTestPlan(individualDeductible: 0, individualOopMax: 3000);
        var engine = CreateEngine(plan, categoryCode: "48", existingOop: 2980m);
        var request = AsSecondary(
            CreateRequest(plan.Id, lines: ("99223", 5000m, 3000m, "21")),
            complementary, (1, 2500m));

        var result = await engine.CalculateAsync(request);

        var line = result.Lines.Single();
        Assert.Equal(expectedPaid, line.PlanPaidAmount);
        Assert.Equal(expectedCoinsurance, line.CoinsuranceAmount);
        Assert.Equal(expectedCoinsurance, line.MemberResponsibility);
        Assert.Equal(580m, line.OopMaxReduction);
        var expectedCas = new List<(string, string, decimal)> { ("CO", "45", 2000m) };
        if (expectedCoinsurance > 0) expectedCas.Add(("PR", "2", expectedCoinsurance));
        expectedCas.Add(("OA", "23", expectedOa23));
        Assert.Equal(expectedCas, line.Adjustments.Select(a => (a.GroupCode, a.ReasonCode, a.Amount)));
        AssertCasInvariants(line);
        Assert.Equal(expectedCoinsurance, line.OopAppliedAmount);
        Assert.Equal(expectedCoinsurance,
            Snapshot(result, AccumulatorType.IndividualOutOfPocketMax).AmountApplied);
    }

    /// <summary>
    /// DRG stay as secondary payer. Pre-COB: $500 deductible + 20% × $11,500
    /// = $2,300 coinsurance; member $2,800, plan $9,200 on $12,000 allowed.
    /// COB is computed once for the stay against the primary's total payment
    /// (Σ per-line amounts), then the reduced PR components and the OA-23
    /// are split across the lines by allowed (truncated to the cent,
    /// remainder on the last line).
    /// </summary>
    [Theory]
    // Complementary, primary $2,000: pays $9,200; member owes 12,000 − 2,000 − 9,200 = $800.
    [InlineData(true, 2000, 9200, 500, 300, 2000)]
    // Non-duplication, primary $2,000: pays 9,200 − 2,000 = $7,200; member keeps $2,800.
    [InlineData(false, 2000, 7200, 500, 2300, 2000)]
    // Non-duplication, primary $10,000 ≥ max benefit: pays $0; member owes $2,000.
    [InlineData(false, 10000, 0, 500, 1500, 10000)]
    public async Task Cob_Drg_ClaimLevelCob_SplitAcrossLines_Balances(
        bool complementary, decimal primaryTotal, decimal expectedPaid,
        decimal expectedDeductible, decimal expectedCoinsurance, decimal expectedOa23)
    {
        var plan = CreateTestPlan(
            individualDeductible: 500,
            individualOopMax: 6000,
            inpatientMethod: InpatientPricingMethod.DrgCaseRate);
        var engine = CreateEngine(plan, categoryCode: "48");
        var request = AsSecondary(
            CreateRequest(plan.Id,
                claimType: "837I", drgCode: "470", drgAllowedAmount: 12000m,
                lines:
                [
                    ("99223", 12000m, 3388.23m, "21"),
                    ("", 1500m, 423.52m, "21"),
                    ("", 29000m, 8188.25m, "21"),
                ]),
            complementary, (1, primaryTotal * 0.25m), (3, primaryTotal * 0.75m));

        var result = await engine.CalculateAsync(request);

        Assert.True(result.Success);
        var drg = result.DrgCostShare!;
        var member = expectedDeductible + expectedCoinsurance;
        Assert.Equal(expectedPaid, drg.PlanPaidAmount);
        Assert.Equal(expectedDeductible, drg.DeductibleAmount);
        Assert.Equal(expectedCoinsurance, drg.CoinsuranceAmount);
        Assert.Equal(member, drg.MemberResponsibility);
        Assert.Equal(expectedOa23, Oa23(drg.Adjustments));
        Assert.All(drg.Adjustments, a => Assert.True(a.Amount >= 0));
        Assert.Equal(member, drg.Adjustments.Where(a => a.GroupCode == "PR").Sum(a => a.Amount));
        Assert.Equal(drg.PlanPaidAmount,
            result.Lines.Sum(l => l.BilledAmount) - drg.Adjustments.Sum(a => a.Amount));

        // Lines: every line balances with ΣPR = member responsibility, and
        // the per-line amounts reconcile to the claim level.
        Assert.All(result.Lines, AssertCasInvariants);
        Assert.Equal(expectedPaid, result.Lines.Sum(l => l.PlanPaidAmount));
        Assert.Equal(expectedOa23, result.Lines.Sum(l => Oa23(l.Adjustments)));
        Assert.Equal(expectedDeductible, result.Lines.Sum(l => l.DeductibleAmount));
        Assert.Equal(expectedCoinsurance, result.Lines.Sum(l => l.CoinsuranceAmount));
        Assert.Equal(member, result.Totals.TotalMemberResponsibility);
        Assert.Equal(member, result.Totals.TotalOopApplied);

        Assert.All(result.Lines, l => Assert.True(l.PlanPaidAmount >= 0));
        var oa23ByLine = result.Lines.Select(l => Oa23(l.Adjustments)).ToArray();
        if (expectedPaid > 0)
        {
            // OA-23 split by allowed: truncated to the cent, remainder on the last line.
            var weights = new[] { 3388.23m, 423.52m, 8188.25m };
            for (var i = 0; i < 2; i++)
                Assert.Equal(Math.Floor(expectedOa23 * weights[i] / 12000m * 100m) / 100m, oa23ByLine[i]);
            Assert.Equal(expectedOa23 - oa23ByLine[0] - oa23ByLine[1], oa23ByLine[2]);
        }
        else
        {
            // The plan pays nothing: each line's OA-23 is capped at its
            // allowed − member share (spilling the rounding back from the
            // last line), so every line pays exactly zero.
            Assert.All(result.Lines, l => Assert.Equal(0m, l.PlanPaidAmount));
        }

        // Accumulators record what the member owes after COB.
        Assert.Equal(expectedDeductible, Snapshot(result, AccumulatorType.IndividualDeductible).AmountApplied);
        Assert.Equal(member, Snapshot(result, AccumulatorType.IndividualOutOfPocketMax).AmountApplied);
    }

    /// <summary>
    /// DRG stay with the OOP cap AND COB. $500 OOP left: pre-COB the
    /// coinsurance is forgiven entirely, the member owes the $500 deductible
    /// and the plan would pay $11,500. The primary paid $7,000 in total.
    /// Complementary pays $5,000 (= $12,000 allowed − $7,000, NAIC MDL-120
    /// §7): nothing is left for the member, OA-23 = $7,000, and the OOP
    /// accumulator does not move. Non-duplication pays $11,500 − $7,000 =
    /// $4,500; the member still owes $500, OA-23 = $7,000. Either way the
    /// deductible accumulator gets the $500 the plan would have applied
    /// alone (NAIC full credit, the default). Line 2 has $0 allowed, so its
    /// OA-23 share is capped at $0 and the whole OA-23 lands on line 1 (no
    /// line pays below zero).
    /// </summary>
    [Theory]
    [InlineData(true, 5000, 0, 7000)]
    [InlineData(false, 4500, 500, 7000)]
    public async Task Cob_Drg_WithOopMax_ClaimLevelAndLinesBalance(
        bool complementary, decimal expectedPaid, decimal expectedMember, decimal expectedOa23)
    {
        var plan = CreateTestPlan(
            individualDeductible: 500,
            individualOopMax: 3000,
            inpatientMethod: InpatientPricingMethod.DrgCaseRate);
        var engine = CreateEngine(plan, categoryCode: "48", existingOop: 2500m);
        var request = AsSecondary(
            CreateRequest(plan.Id,
                claimType: "837I", drgCode: "470", drgAllowedAmount: 12000m,
                lines:
                [
                    ("99223", 15000m, 12000m, "21"),
                    ("", 5000m, 0m, "21"),
                ]),
            complementary, (1, 6000m), (2, 1000m));

        var result = await engine.CalculateAsync(request);

        Assert.True(result.Success);
        var drg = result.DrgCostShare!;
        Assert.Equal(2300m, drg.OopMaxReduction);
        Assert.Equal(expectedPaid, drg.PlanPaidAmount);
        Assert.Equal(expectedMember, drg.MemberResponsibility);
        Assert.Equal(expectedMember, drg.DeductibleAmount);
        Assert.Equal(0m, drg.CoinsuranceAmount);
        Assert.Equal(expectedOa23, Oa23(drg.Adjustments));
        Assert.All(drg.Adjustments, a => Assert.True(a.Amount >= 0));
        var totalBilled = result.Lines.Sum(l => l.BilledAmount);
        Assert.Equal(drg.PlanPaidAmount, totalBilled - drg.Adjustments.Sum(a => a.Amount));

        Assert.All(result.Lines, AssertCasInvariants);
        Assert.Equal(drg.PlanPaidAmount, result.Lines.Sum(l => l.PlanPaidAmount));
        Assert.Equal(expectedOa23, Oa23(result.Lines[0].Adjustments));
        Assert.Equal(0m, Oa23(result.Lines[1].Adjustments));
        Assert.Equal(0m, result.Lines[1].PlanPaidAmount);

        Assert.Equal(500m, Snapshot(result, AccumulatorType.IndividualDeductible).AmountApplied);
        Assert.Equal(500m, drg.DeductibleCreditedAmount);
        Assert.Equal(500m, result.Lines.Sum(l => l.DeductibleCreditedAmount));
        Assert.Equal(expectedMember, Snapshot(result, AccumulatorType.IndividualOutOfPocketMax).AmountApplied);
    }

    /// <summary>
    /// COB changes only the member amount: deductible met, 20% coinsurance
    /// on $250 allowed (= billed) → member $50, plan $200 pre-COB. The
    /// primary paid $50, so complementary still pays the full $200 (no COB
    /// savings) while the member owes 250 − 50 − 200 = $0. PR-2 is dropped,
    /// OA-23 = allowed − member − paid = $50, the line balances, and no OOP
    /// is accumulated.
    /// </summary>
    [Fact]
    public async Task Cob_OnlyMemberResponsibilityChanges_Line_Oa23IsPrimaryCoveredCostShare()
    {
        var plan = CreateTestPlan(individualDeductible: 500);
        var engine = CreateEngine(plan, categoryCode: "48", existingDeductible: 500m);
        var request = AsSecondary(
            CreateRequest(plan.Id, lines: ("99223", 250m, 250m, "21")),
            complementary: true, (1, 50m));

        var result = await engine.CalculateAsync(request);

        var line = result.Lines.Single();
        Assert.Equal(200m, line.PlanPaidAmount);
        Assert.Equal(0m, line.MemberResponsibility);
        Assert.Equal(
            new[] { ("OA", "23", 50m) },
            line.Adjustments.Select(a => (a.GroupCode, a.ReasonCode, a.Amount)));
        AssertCasInvariants(line);
        Assert.Equal(0m, line.OopAppliedAmount);
        Assert.Equal(0m, Snapshot(result, AccumulatorType.IndividualOutOfPocketMax).AmountApplied);
    }

    /// <summary>
    /// The same member-only change on the DRG path: deductible met, 20% of
    /// the $12,000 stay → member $2,400, plan $9,600. The primary paid
    /// $2,400 in total, so the plan still pays $9,600 and the member owes
    /// $0; OA-23 = $2,400, split by allowed (truncated to the cent, remainder
    /// on the last line), every line balances.
    /// </summary>
    [Fact]
    public async Task Cob_OnlyMemberResponsibilityChanges_Drg_Oa23SplitAndBalances()
    {
        var plan = CreateTestPlan(
            individualDeductible: 500,
            individualOopMax: 6000,
            inpatientMethod: InpatientPricingMethod.DrgCaseRate);
        var engine = CreateEngine(plan, categoryCode: "48", existingDeductible: 500m);
        var request = AsSecondary(
            CreateRequest(plan.Id,
                claimType: "837I", drgCode: "470", drgAllowedAmount: 12000m,
                lines:
                [
                    ("99223", 4000m, 4000m, "21"),
                    ("", 8000m, 8000m, "21"),
                ]),
            complementary: true, (1, 1000m), (2, 1400m));

        var result = await engine.CalculateAsync(request);

        var drg = result.DrgCostShare!;
        Assert.Equal(9600m, drg.PlanPaidAmount);
        Assert.Equal(0m, drg.MemberResponsibility);
        Assert.Equal(2400m, Oa23(drg.Adjustments));
        Assert.DoesNotContain(drg.Adjustments, a => a.GroupCode == "PR");
        Assert.Equal(drg.PlanPaidAmount,
            result.Lines.Sum(l => l.BilledAmount) - drg.Adjustments.Sum(a => a.Amount));

        Assert.All(result.Lines, AssertCasInvariants);
        // Truncated proportional split (2400 × 4000/12000 truncates to 799.99), remainder on the last line.
        Assert.Equal(new[] { 799.99m, 1600.01m }, result.Lines.Select(l => Oa23(l.Adjustments)));
        Assert.Equal(new[] { 3200.01m, 6399.99m }, result.Lines.Select(l => l.PlanPaidAmount));
        Assert.Equal(0m, result.Totals.TotalOopApplied);
        Assert.Equal(0m, Snapshot(result, AccumulatorType.IndividualOutOfPocketMax).AmountApplied);
    }

    /// <summary>A primary claim (no COB) on the DRG path emits no OA-23.</summary>
    [Fact]
    public async Task Drg_Primary_NoCob_NoOa23()
    {
        var plan = CreateTestPlan(
            individualDeductible: 500,
            individualOopMax: 6000,
            inpatientMethod: InpatientPricingMethod.DrgCaseRate);
        var engine = CreateEngine(plan, categoryCode: "48");
        var request = CreateRequest(plan.Id,
            claimType: "837I", drgCode: "470", drgAllowedAmount: 12000m,
            lines: [("99223", 15000m, 12000m, "21")]);

        var result = await engine.CalculateAsync(request);

        Assert.Equal(9200m, result.DrgCostShare!.PlanPaidAmount);
        Assert.DoesNotContain(result.DrgCostShare.Adjustments, a => a.GroupCode == "OA");
        Assert.All(result.Lines, AssertCasInvariants);
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
    // COB — TERTIARY AND LATER: N PRIOR PAYERS
    //
    // priorPaid = Σ every earlier payer's payment; the balance is
    // min(allowed − priorPaid, the last prior payer's PR when reported);
    // standard pays min(normal, balance), non-duplication
    // min(normal − priorPaid, balance); member = min(cost share, balance −
    // paid); one positive OA-23 = allowed − member − paid (NAIC MDL-120
    // §6.A(4), §7).
    // ═══════════════════════════════════════════════════════════════════

    private static PriorPayerAdjudication PriorPayer(
        int sequence, decimal? claimPaid = null,
        (string group, string carc, decimal amount)[]? claimCas = null,
        params (int line, decimal paid, (string group, string carc, decimal amount)[] cas)[] lines)
        => new()
        {
            Sequence = sequence,
            PayerId = $"PAYER-{sequence}",
            ClaimPaidAmount = claimPaid,
            ClaimAdjustments = (claimCas ?? [])
                .Select(c => new PriorPayerAdjustment { GroupCode = c.group, ReasonCode = c.carc, Amount = c.amount })
                .ToList(),
            Lines = lines
                .Select(l => new PriorPayerLineAdjudication
                {
                    LineNumber = l.line,
                    PaidAmount = l.paid,
                    Adjustments = l.cas
                        .Select(c => new PriorPayerAdjustment { GroupCode = c.group, ReasonCode = c.carc, Amount = c.amount })
                        .ToList(),
                })
                .ToList(),
        };

    private static BenefitResolutionRequest WithPriorPayers(
        BenefitResolutionRequest request, int ourSequence, bool complementary, params PriorPayerAdjudication[] priorPayers)
        => request with
        {
            Cob = new CobInfo
            {
                PayerSequence = ourSequence,
                UseComplementaryModel = complementary,
                PriorPayers = priorPayers.ToList(),
            }
        };

    private static (string, string, decimal)[] Cas(LineBenefitResult l) =>
        l.Adjustments.Select(a => (a.GroupCode, a.ReasonCode, a.Amount)).ToArray();

    /// <summary>
    /// Tertiary, both prior payers paying, line level (2430). Deductible met;
    /// we allow $150 of the $200 charge: copay $30 + coinsurance $24, normal
    /// benefit $96.
    /// Primary: paid $80, PR-1 $40 (CO-45 $80). Secondary: paid $25, PR-3 $15
    /// (OA-23 $160). Prior paid $105; the member owes $15 after the
    /// secondary, so balance = min(150 − 105, 15) = $15.
    /// Standard: we pay $15, member $0, OA-23 = 150 − 0 − 15 = $135.
    /// Non-duplication: 96 − 105 &lt; 0 → we pay $0; member min(54, 15) = $15
    /// (coinsurance forgiven first, PR-3 $15), OA-23 = 150 − 15 − 0 = $135.
    /// </summary>
    [Theory]
    [InlineData(true, 15, 0, 0)]
    [InlineData(false, 0, 15, 0)]
    public async Task Cob_Tertiary_BothPriorPayersPaid_Line(
        bool complementary, decimal expectedPaid, decimal expectedCopay, decimal expectedCoinsurance)
    {
        var plan = CreateTestPlan(individualDeductible: 500);
        var engine = CreateEngine(plan, categoryCode: "98", existingDeductible: 500m);
        var request = WithPriorPayers(
            CreateRequest(plan.Id, lines: ("99213", 200m, 150m, "11")),
            ourSequence: 3, complementary,
            PriorPayer(1, claimPaid: 80m, lines: (1, 80m, [("CO", "45", 80m), ("PR", "1", 40m)])),
            PriorPayer(2, claimPaid: 25m, lines: (1, 25m, [("OA", "23", 160m), ("PR", "3", 15m)])));

        var result = await engine.CalculateAsync(request);

        var line = result.Lines.Single();
        Assert.Equal(expectedPaid, line.PlanPaidAmount);
        Assert.Equal(expectedCopay, line.CopayAmount);
        Assert.Equal(expectedCoinsurance, line.CoinsuranceAmount);
        Assert.Equal(expectedCopay + expectedCoinsurance, line.MemberResponsibility);
        Assert.Equal(135m, Oa23(line.Adjustments));
        Assert.Single(line.Adjustments, a => a is { GroupCode: "OA", ReasonCode: "23" });
        AssertCasInvariants(line);
        // Never more than allowed − total prior paid.
        Assert.True(line.PlanPaidAmount <= 150m - 105m);
        Assert.Equal(line.MemberResponsibility, Snapshot(result, AccumulatorType.IndividualOutOfPocketMax).AmountApplied);
    }

    /// <summary>
    /// Tertiary where the secondary paid $0 (it applied everything to its
    /// own deductible). Primary paid $80, PR $40; secondary paid $0, PR-1
    /// $40. Prior paid $80; balance = min(150 − 80, 40) = $40.
    /// Standard: we pay $40, member $0, OA-23 $110.
    /// Non-duplication: we pay 96 − 80 = $16; member min(54, 40 − 16) = $24
    /// (coinsurance $24 forgiven to 0, copay $30 → $24), OA-23 $110.
    /// </summary>
    [Theory]
    [InlineData(true, 40, 0)]
    [InlineData(false, 16, 24)]
    public async Task Cob_Tertiary_SecondaryPaidZero_Line(
        bool complementary, decimal expectedPaid, decimal expectedMember)
    {
        var plan = CreateTestPlan(individualDeductible: 500);
        var engine = CreateEngine(plan, categoryCode: "98", existingDeductible: 500m);
        var request = WithPriorPayers(
            CreateRequest(plan.Id, lines: ("99213", 200m, 150m, "11")),
            ourSequence: 3, complementary,
            PriorPayer(1, claimPaid: 80m, lines: (1, 80m, [("CO", "45", 80m), ("PR", "1", 40m)])),
            PriorPayer(2, claimPaid: 0m, lines: (1, 0m, [("CO", "45", 80m), ("OA", "23", 80m), ("PR", "1", 40m)])));

        var result = await engine.CalculateAsync(request);

        var line = result.Lines.Single();
        Assert.Equal(expectedPaid, line.PlanPaidAmount);
        Assert.Equal(expectedMember, line.MemberResponsibility);
        Assert.Equal(expectedMember, line.CopayAmount);
        Assert.Equal(0m, line.CoinsuranceAmount);
        Assert.Equal(110m, Oa23(line.Adjustments));
        AssertCasInvariants(line);
    }

    /// <summary>
    /// Prior payers reported without any CAS (patient responsibility
    /// unknown): the balance is allowed − total prior paid. Primary $60 +
    /// secondary $40 = $100 on $150 allowed → standard pays $50 (not its
    /// normal $96), member $0, OA-23 $100.
    /// </summary>
    [Fact]
    public async Task Cob_Tertiary_NoPriorPatientResponsibility_CapsAtAllowedLessPriorPaid()
    {
        var plan = CreateTestPlan(individualDeductible: 500);
        var engine = CreateEngine(plan, categoryCode: "98", existingDeductible: 500m);
        var request = WithPriorPayers(
            CreateRequest(plan.Id, lines: ("99213", 200m, 150m, "11")),
            ourSequence: 3, complementary: true,
            PriorPayer(1, claimPaid: 60m),
            PriorPayer(2, claimPaid: 40m));

        var result = await engine.CalculateAsync(request);

        var line = result.Lines.Single();
        Assert.Equal(50m, line.PlanPaidAmount);
        Assert.Equal(0m, line.MemberResponsibility);
        Assert.Equal(new[] { ("CO", "45", 50m), ("OA", "23", 100m) }, Cas(line));
        AssertCasInvariants(line);
    }

    /// <summary>
    /// Payers sequenced at or after us (an 837 to the secondary may list the
    /// tertiary in 2320 too) are ignored: as secondary, only the primary's
    /// $40 counts.
    /// </summary>
    [Fact]
    public async Task Cob_LaterPayersOnClaim_AreIgnored()
    {
        var plan = CreateTestPlan(individualDeductible: 500);
        var engine = CreateEngine(plan, categoryCode: "98", existingDeductible: 500m);
        var request = WithPriorPayers(
            CreateRequest(plan.Id, lines: ("99213", 200m, 150m, "11")),
            ourSequence: 2, complementary: true,
            PriorPayer(1, claimPaid: 40m),
            PriorPayer(3, claimPaid: 100m));

        var result = await engine.CalculateAsync(request);

        var line = result.Lines.Single();
        Assert.Equal(96m, line.PlanPaidAmount);   // min(96, 150 − 40)
        Assert.Equal(14m, line.MemberResponsibility); // 150 − 40 − 96
        Assert.Equal(40m, Oa23(line.Adjustments));
        AssertCasInvariants(line);
    }

    /// <summary>
    /// Line-level and claim-level prior payments mixed. Two lines:
    /// L1 billed $200 / allowed $150 (normal $96), L2 billed $100 / allowed
    /// $75 (copay $30, coinsurance 20% × 45 = $9, normal $36).
    /// Primary reported L1 in 2430 (paid $80, PR-1 $40) and AMT*D $120 with a
    /// claim-level PR-2 $10: the $40 not on a 2430 line and the $10 PR go to
    /// L2, the only unreported line.
    /// Secondary reported at claim level only: AMT*D $30, PR-1 $30, prorated
    /// by charge 200 : 100 → L1 $20 / PR $20, L2 $10 / PR $10.
    /// L1: prior paid 100, balance min(150 − 100, 20) = $20 → pay $20, OA-23 $130.
    /// L2: prior paid 50, balance min(75 − 50, 10) = $10 → pay $10, OA-23 $65.
    /// </summary>
    [Fact]
    public async Task Cob_Tertiary_LineAndClaimLevelPriorPaymentsMixed()
    {
        var plan = CreateTestPlan(individualDeductible: 500);
        var engine = CreateEngine(plan, categoryCode: "98", existingDeductible: 500m);
        var request = WithPriorPayers(
            CreateRequest(plan.Id, lines: [("99213", 200m, 150m, "11"), ("99212", 100m, 75m, "11")]),
            ourSequence: 3, complementary: true,
            PriorPayer(1, claimPaid: 120m, claimCas: [("PR", "2", 10m)],
                lines: (1, 80m, [("CO", "45", 80m), ("PR", "1", 40m)])),
            PriorPayer(2, claimPaid: 30m, claimCas: [("PR", "1", 30m), ("OA", "23", 240m)]));

        var result = await engine.CalculateAsync(request);

        Assert.Equal(new[] { 20m, 10m }, result.Lines.Select(l => l.PlanPaidAmount));
        Assert.Equal(new[] { 130m, 65m }, result.Lines.Select(l => Oa23(l.Adjustments)));
        Assert.All(result.Lines, l => Assert.Equal(0m, l.MemberResponsibility));
        Assert.All(result.Lines, AssertCasInvariants);
        Assert.Equal(30m, result.Totals.TotalPlanPaid);
    }

    /// <summary>
    /// DRG stay, tertiary. Pre-COB: deductible $500 + coinsurance 20% ×
    /// 11,500 = $2,300; member $2,800, normal $9,200 on $12,000 allowed.
    /// Primary: claim level only, AMT*D $6,000. Secondary: 2430 on every
    /// line, paid 1,000 / 0 / 2,000 = $3,000, PR 300 / 50 / 650 = $1,000.
    /// Prior paid $9,000; balance = min(12,000 − 9,000, 1,000) = $1,000.
    /// Standard: pay $1,000, member $0, OA-23 $11,000.
    /// Non-duplication: pay min(9,200 − 9,000, 1,000) = $200; member
    /// min(2,800, 800) = $800 (coinsurance 2,300 → 300, deductible $500),
    /// OA-23 $11,000.
    /// The deductible accumulator gets the $500 our plan applied alone (NAIC
    /// full credit); the OOP accumulator the member's share.
    /// </summary>
    [Theory]
    [InlineData(true, 1000, 0, 0)]
    [InlineData(false, 200, 500, 300)]
    public async Task Cob_Tertiary_Drg_ClaimLevelCob_Balances(
        bool complementary, decimal expectedPaid, decimal expectedDeductible, decimal expectedCoinsurance)
    {
        var plan = CreateTestPlan(
            individualDeductible: 500, individualOopMax: 6000,
            inpatientMethod: InpatientPricingMethod.DrgCaseRate);
        var engine = CreateEngine(plan, categoryCode: "48");
        var request = WithPriorPayers(
            CreateRequest(plan.Id,
                claimType: "837I", drgCode: "470", drgAllowedAmount: 12000m,
                lines:
                [
                    ("99223", 12000m, 3388.23m, "21"),
                    ("", 1500m, 423.52m, "21"),
                    ("", 29000m, 8188.25m, "21"),
                ]),
            ourSequence: 3, complementary,
            PriorPayer(1, claimPaid: 6000m),
            PriorPayer(2, claimPaid: 3000m, lines:
            [
                (1, 1000m, [("PR", "2", 300m)]),
                (2, 0m, [("PR", "2", 50m)]),
                (3, 2000m, [("PR", "2", 650m)]),
            ]));

        var result = await engine.CalculateAsync(request);

        Assert.True(result.Success);
        var drg = result.DrgCostShare!;
        var member = expectedDeductible + expectedCoinsurance;
        Assert.Equal(expectedPaid, drg.PlanPaidAmount);
        Assert.Equal(member, drg.MemberResponsibility);
        Assert.Equal(11000m, Oa23(drg.Adjustments));
        Assert.Equal(drg.PlanPaidAmount, result.Lines.Sum(l => l.BilledAmount) - drg.Adjustments.Sum(a => a.Amount));
        Assert.All(drg.Adjustments, a => Assert.True(a.Amount >= 0));

        Assert.All(result.Lines, AssertCasInvariants);
        Assert.All(result.Lines, l => Assert.True(l.PlanPaidAmount >= 0));
        Assert.Equal(expectedPaid, result.Lines.Sum(l => l.PlanPaidAmount));
        Assert.Equal(11000m, result.Lines.Sum(l => Oa23(l.Adjustments)));
        Assert.Equal(expectedDeductible, result.Lines.Sum(l => l.DeductibleAmount));
        Assert.Equal(expectedCoinsurance, result.Lines.Sum(l => l.CoinsuranceAmount));

        Assert.Equal(500m, Snapshot(result, AccumulatorType.IndividualDeductible).AmountApplied);
        Assert.Equal(500m, result.Lines.Sum(l => l.DeductibleCreditedAmount));
        Assert.Equal(member, Snapshot(result, AccumulatorType.IndividualOutOfPocketMax).AmountApplied);
    }

    // ═══════════════════════════════════════════════════════════════════
    // COB DEDUCTIBLE CREDIT (per plan): NaicFullCredit / MemberPaidOnly /
    // NoDeductible — secondary and tertiary, line and DRG, deductible
    // partly met. NAIC MDL-120 §7: "the secondary plan shall credit to its
    // plan deductible any amounts it would have credited to its deductible
    // in the absence of other health care coverage." OOP: always the
    // member's share after COB.
    // ═══════════════════════════════════════════════════════════════════

    private static BenefitResolutionRequest AsLaterPayer(BenefitResolutionRequest request, int ourSequence, decimal totalPriorPaid)
        => ourSequence == 2
            ? WithPriorPayers(request, 2, complementary: true, PriorPayer(1, claimPaid: totalPriorPaid))
            : WithPriorPayers(request, ourSequence, complementary: true,
                PriorPayer(1, claimPaid: totalPriorPaid * 0.6m),
                PriorPayer(2, claimPaid: totalPriorPaid * 0.4m));

    /// <summary>
    /// $400 of the $500 deductible met ($100 left). Line billed $200,
    /// allowed $150: pre-COB deductible $100, copay $30, coinsurance 20% × 20
    /// = $4 → member $134, normal $16. Prior payers paid $100 in total.
    /// NaicFullCredit / MemberPaidOnly — same 835: we pay min(16, 50) = $16;
    /// member min(134, 34) = $34, all deductible (PR-1 $34), OA-23 $100. The
    /// deductible accumulator gets $100 (NAIC: what we applied alone, capped
    /// by the $100 remaining) or $34 (member-paid only). OOP $34 either way.
    /// NoDeductible: no deductible — copay $30 + coinsurance $24, normal $96;
    /// we pay min(96, 50) = $50, member $0, OA-23 $100, nothing credited.
    /// </summary>
    [Theory]
    [InlineData(2, CobDeductibleCredit.NaicFullCredit, 16, 34, 100, 34)]
    [InlineData(3, CobDeductibleCredit.NaicFullCredit, 16, 34, 100, 34)]
    [InlineData(2, CobDeductibleCredit.MemberPaidOnly, 16, 34, 34, 34)]
    [InlineData(3, CobDeductibleCredit.MemberPaidOnly, 16, 34, 34, 34)]
    [InlineData(2, CobDeductibleCredit.NoDeductible, 50, 0, 0, 0)]
    [InlineData(3, CobDeductibleCredit.NoDeductible, 50, 0, 0, 0)]
    public async Task CobDeductibleCredit_Line_PartlyMetDeductible(
        int ourSequence, CobDeductibleCredit credit,
        decimal expectedPaid, decimal expectedPr1, decimal expectedDeductibleCredit, decimal expectedOop)
    {
        var plan = CreateTestPlan(individualDeductible: 500) with { CobDeductibleCredit = credit };
        var engine = CreateEngine(plan, categoryCode: "98", existingDeductible: 400m);
        var request = AsLaterPayer(CreateRequest(plan.Id, lines: ("99213", 200m, 150m, "11")), ourSequence, 100m);

        var result = await engine.CalculateAsync(request);

        var line = result.Lines.Single();
        Assert.Equal(expectedPaid, line.PlanPaidAmount);
        Assert.Equal(expectedPr1, line.DeductibleAmount);
        Assert.Equal(expectedPr1, line.MemberResponsibility);
        Assert.Equal(100m, Oa23(line.Adjustments));
        AssertCasInvariants(line);
        Assert.Equal(expectedDeductibleCredit, line.DeductibleCreditedAmount);
        Assert.Equal(expectedDeductibleCredit, Snapshot(result, AccumulatorType.IndividualDeductible).AmountApplied);
        Assert.Equal(expectedDeductibleCredit, Snapshot(result, AccumulatorType.FamilyDeductible, AccumulatorScope.Family).AmountApplied);
        Assert.Equal(expectedOop, Snapshot(result, AccumulatorType.IndividualOutOfPocketMax).AmountApplied);
        Assert.Equal(expectedOop, line.OopAppliedAmount);
    }

    /// <summary>
    /// NaicFullCredit and MemberPaidOnly produce the same 835: the claim is
    /// always priced as if this plan were the only plan (NAIC MDL-120 §7),
    /// so the setting changes only the deductible accumulators. (Compared
    /// with line-by-line COB before claim-level COB, multi-line secondary /
    /// tertiary 835s do change — see the MultiLine tests; NoDeductible changes
    /// the 835 by not applying the deductible.) $300 of deductible left. Line: allowed $300 all
    /// deductible, prior payers $150 → we pay $0, member $150 (PR-1);
    /// credited $300 vs $150. DRG: deductible $300 + coinsurance $2,340,
    /// prior payers $4,000 → we pay $8,000, member $0; credited $300 vs $0.
    /// </summary>
    [Theory]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    public async Task CobDeductibleCredit_NaicAndMemberPaidOnly_ProduceTheSame835(int ourSequence, bool drg)
    {
        async Task<BenefitResolutionResult> Run(CobDeductibleCredit credit)
        {
            var plan = CreateTestPlan(
                individualDeductible: 500, individualOopMax: 6000,
                inpatientMethod: drg ? InpatientPricingMethod.DrgCaseRate : InpatientPricingMethod.PerLine)
                with { CobDeductibleCredit = credit };
            var engine = CreateEngine(plan, categoryCode: drg ? "48" : "98", existingDeductible: 200m);
            var request = drg
                ? CreateRequest(plan.Id, claimType: "837I", drgCode: "470", drgAllowedAmount: 12000m,
                    lines: [("99223", 12000m, 6000m, "21"), ("", 8000m, 6000m, "21")])
                : CreateRequest(plan.Id, lines: ("99213", 400m, 300m, "11"));
            return await engine.CalculateAsync(AsLaterPayer(request, ourSequence, drg ? 4000m : 150m));
        }

        var naic = await Run(CobDeductibleCredit.NaicFullCredit);
        var memberPaid = await Run(CobDeductibleCredit.MemberPaidOnly);

        Assert.Equal(naic.Lines.Select(Cas), memberPaid.Lines.Select(Cas));
        Assert.Equal(naic.Lines.Select(l => l.PlanPaidAmount), memberPaid.Lines.Select(l => l.PlanPaidAmount));
        Assert.Equal(naic.Totals.TotalMemberResponsibility, memberPaid.Totals.TotalMemberResponsibility);
        Assert.Equal(naic.Totals.TotalDeductible, memberPaid.Totals.TotalDeductible);
        Assert.All(naic.Lines, AssertCasInvariants);
        // NAIC credits the full $300 left; member-paid only what PR-1 shows.
        Assert.Equal(300m, Snapshot(naic, AccumulatorType.IndividualDeductible).AmountApplied);
        Assert.Equal(memberPaid.Totals.TotalDeductible, Snapshot(memberPaid, AccumulatorType.IndividualDeductible).AmountApplied);
        Assert.True(memberPaid.Totals.TotalDeductible < 300m);
        Assert.Equal(
            Snapshot(naic, AccumulatorType.IndividualOutOfPocketMax).AmountApplied,
            Snapshot(memberPaid, AccumulatorType.IndividualOutOfPocketMax).AmountApplied);
    }

    /// <summary>
    /// DRG stay, $200 of the $500 deductible met ($300 left). Pre-COB:
    /// deductible $300, coinsurance 20% × 11,700 = $2,340; normal $9,360.
    /// Prior payers paid $10,000: we pay min(9,360, 2,000) = $2,000; the
    /// member owes $0. The deductible accumulator gets $300 under NAIC full
    /// credit, $0 under member-paid only. NoDeductible: coinsurance 20% ×
    /// 12,000 = $2,400, normal $9,600 → we pay $2,000, nothing credited.
    /// </summary>
    [Theory]
    [InlineData(2, CobDeductibleCredit.NaicFullCredit, 300)]
    [InlineData(3, CobDeductibleCredit.NaicFullCredit, 300)]
    [InlineData(2, CobDeductibleCredit.MemberPaidOnly, 0)]
    [InlineData(3, CobDeductibleCredit.MemberPaidOnly, 0)]
    [InlineData(2, CobDeductibleCredit.NoDeductible, 0)]
    [InlineData(3, CobDeductibleCredit.NoDeductible, 0)]
    public async Task CobDeductibleCredit_Drg_PartlyMetDeductible(
        int ourSequence, CobDeductibleCredit credit, decimal expectedDeductibleCredit)
    {
        var plan = CreateTestPlan(
            individualDeductible: 500, individualOopMax: 6000,
            inpatientMethod: InpatientPricingMethod.DrgCaseRate) with { CobDeductibleCredit = credit };
        var engine = CreateEngine(plan, categoryCode: "48", existingDeductible: 200m);
        var request = AsLaterPayer(
            CreateRequest(plan.Id, claimType: "837I", drgCode: "470", drgAllowedAmount: 12000m,
                lines: [("99223", 12000m, 3388.23m, "21"), ("", 1500m, 423.52m, "21"), ("", 29000m, 8188.25m, "21")]),
            ourSequence, 10000m);

        var result = await engine.CalculateAsync(request);

        var drg = result.DrgCostShare!;
        Assert.Equal(2000m, drg.PlanPaidAmount);
        Assert.Equal(0m, drg.MemberResponsibility);
        Assert.Equal(10000m, Oa23(drg.Adjustments));
        Assert.All(result.Lines, AssertCasInvariants);
        Assert.Equal(expectedDeductibleCredit, drg.DeductibleCreditedAmount);
        Assert.Equal(expectedDeductibleCredit, result.Lines.Sum(l => l.DeductibleCreditedAmount));
        Assert.Equal(expectedDeductibleCredit, result.Totals.TotalDeductibleCredited);
        Assert.Equal(expectedDeductibleCredit, Snapshot(result, AccumulatorType.IndividualDeductible).AmountApplied);
        Assert.Equal(0m, Snapshot(result, AccumulatorType.IndividualOutOfPocketMax).AmountApplied);
    }

    /// <summary>
    /// Multi-line SECONDARY claim, $300 of the deductible left. The claim is
    /// priced first as if this plan were the only plan (NAIC MDL-120 §7), so
    /// the deductible met on line 1 is met for line 2 too; COB is then applied
    /// to the claim as a whole.
    /// L1 (billed 400, allowed 300): deductible $300 → cost share 300, normal $0.
    /// L2 (billed 200, allowed 100): copay $30 + coinsurance 20% × 70 = $14 →
    /// cost share 44, normal $56.
    /// Prior payer $150 at claim level, no CAS (PR unknown) → by charge $100
    /// on L1, $50 on L2. Room 200 + 50 = $250; no bound → balance $250.
    /// Claim: pay min(56, 250) = $56; member min(344, 250 − 56) = $194.
    /// (Line-level capping would pay only $50: L2's normal $56 does not fit
    /// in its $50 room — the reviewer's M1.)
    /// Lines: payment by normal benefit up to min(normal, room) — L2 $50 —
    /// then the $6 left to L1's room; the member's $194 on L1 (PR-1).
    /// L1: paid 6, PR-1 194, OA-23 = 300 − 194 − 6 = 100. L2: paid 50,
    /// OA-23 50. The 835 is the same under MemberPaidOnly (the setting only
    /// changes the accumulators): deductible credited 300 (NAIC: what the
    /// plan applied alone) vs 194 (PR-1); OOP 194 either way.
    /// </summary>
    [Fact]
    public async Task CobDeductibleCredit_MultiLineSecondary_ClaimLevelCob()
    {
        async Task<BenefitResolutionResult> Run(CobDeductibleCredit credit)
        {
            var plan = CreateTestPlan(individualDeductible: 500) with { CobDeductibleCredit = credit };
            var engine = CreateEngine(plan, categoryCode: "98", existingDeductible: 200m);
            return await engine.CalculateAsync(AsLaterPayer(
                CreateRequest(plan.Id, lines: [("99213", 400m, 300m, "11"), ("99212", 200m, 100m, "11")]), 2, 150m));
        }

        var naic = await Run(CobDeductibleCredit.NaicFullCredit);
        Assert.Equal(new[] { 6m, 50m }, naic.Lines.Select(l => l.PlanPaidAmount));
        Assert.Equal(new[] { 194m, 0m }, naic.Lines.Select(l => l.DeductibleAmount));
        Assert.Equal(new[] { 194m, 0m }, naic.Lines.Select(l => l.MemberResponsibility));
        Assert.Equal(new[] { ("CO", "45", 100m), ("PR", "1", 194m), ("OA", "23", 100m) }, Cas(naic.Lines[0]));
        Assert.Equal(new[] { ("CO", "45", 100m), ("OA", "23", 50m) }, Cas(naic.Lines[1]));
        Assert.Equal(new[] { 300m, 0m }, naic.Lines.Select(l => l.DeductibleCreditedAmount));
        Assert.Equal(300m, Snapshot(naic, AccumulatorType.IndividualDeductible).AmountApplied);
        Assert.Equal(194m, Snapshot(naic, AccumulatorType.IndividualOutOfPocketMax).AmountApplied);
        Assert.Equal(56m, naic.Totals.TotalPlanPaid);
        Assert.Equal(194m, naic.Totals.TotalMemberResponsibility);
        Assert.Equal(2, naic.CobPayerSequence);

        var memberPaid = await Run(CobDeductibleCredit.MemberPaidOnly);
        Assert.Equal(naic.Lines.Select(Cas), memberPaid.Lines.Select(Cas));
        Assert.Equal(new[] { 194m, 0m }, memberPaid.Lines.Select(l => l.DeductibleCreditedAmount));
        Assert.Equal(194m, Snapshot(memberPaid, AccumulatorType.IndividualDeductible).AmountApplied);
        Assert.Equal(194m, Snapshot(memberPaid, AccumulatorType.IndividualOutOfPocketMax).AmountApplied);

        Assert.All(naic.Lines, AssertCasInvariants);
        Assert.All(memberPaid.Lines, AssertCasInvariants);
        Assert.Equal(naic.Totals.TotalPlanPaid,
            naic.Lines.Sum(l => l.BilledAmount - l.Adjustments.Sum(a => a.Amount)));
        // All payers together never exceed the claim's allowable expense.
        Assert.True(150m + naic.Totals.TotalPlanPaid + naic.Totals.TotalMemberResponsibility <= 400m);
    }

    /// <summary>
    /// Multi-line TERTIARY claim, $150 of the $500 deductible left. Two lines
    /// billed $200 / allowed $150. Pre-COB (as the only plan): L1 deductible
    /// $150, normal $0; L2 (deductible now met) copay $30 + coinsurance 20% ×
    /// 120 = $24, normal $96.
    /// Primary (2430): paid $60, PR-1 $40 on each line. Secondary (2320 only):
    /// AMT*D $40, PR-1 $40 → $20 paid per line (by charge 1 : 1); its $40 PR
    /// is the member's liability after both payers for the whole claim.
    /// Prior paid $80 per line; room 70 + 70 = $140; the secondary adjudicated
    /// both lines, so balance = min(140, 40) = $40 (split 20 / 20 by room).
    /// Claim: pay min(96, 40) = $40, member $0. (Line-level capping with the
    /// PR prorated $20 per line paid only $20: L1's normal is $0.)
    /// L1: paid 20, OA-23 130. L2: paid 20, OA-23 130.
    /// Deductible credited: NAIC $150 (L1), member-paid-only $0.
    /// </summary>
    [Fact]
    public async Task CobDeductibleCredit_MultiLineTertiary_ClaimLevelCob()
    {
        async Task<BenefitResolutionResult> Run(CobDeductibleCredit credit)
        {
            var plan = CreateTestPlan(individualDeductible: 500) with { CobDeductibleCredit = credit };
            var engine = CreateEngine(plan, categoryCode: "98", existingDeductible: 350m);
            return await engine.CalculateAsync(WithPriorPayers(
                CreateRequest(plan.Id, lines: [("99213", 200m, 150m, "11"), ("99214", 200m, 150m, "11")]),
                ourSequence: 3, complementary: true,
                PriorPayer(1, claimPaid: 120m, lines:
                [
                    (1, 60m, [("CO", "45", 100m), ("PR", "1", 40m)]),
                    (2, 60m, [("CO", "45", 100m), ("PR", "1", 40m)]),
                ]),
                PriorPayer(2, claimPaid: 40m, claimCas: [("PR", "1", 40m), ("OA", "23", 320m)])));
        }

        var naic = await Run(CobDeductibleCredit.NaicFullCredit);
        Assert.Equal(new[] { 20m, 20m }, naic.Lines.Select(l => l.PlanPaidAmount));
        Assert.Equal(new[] { 0m, 0m }, naic.Lines.Select(l => l.MemberResponsibility));
        Assert.Equal(new[] { ("CO", "45", 50m), ("OA", "23", 130m) }, Cas(naic.Lines[0]));
        Assert.Equal(new[] { ("CO", "45", 50m), ("OA", "23", 130m) }, Cas(naic.Lines[1]));
        Assert.Equal(new[] { 150m, 0m }, naic.Lines.Select(l => l.DeductibleCreditedAmount));
        Assert.Equal(150m, Snapshot(naic, AccumulatorType.IndividualDeductible).AmountApplied);
        Assert.Equal(0m, Snapshot(naic, AccumulatorType.IndividualOutOfPocketMax).AmountApplied);
        Assert.Equal(40m, naic.Totals.TotalPlanPaid);
        Assert.Equal(3, naic.CobPayerSequence);

        var memberPaid = await Run(CobDeductibleCredit.MemberPaidOnly);
        Assert.Equal(naic.Lines.Select(Cas), memberPaid.Lines.Select(Cas));
        Assert.Equal(0m, Snapshot(memberPaid, AccumulatorType.IndividualDeductible).AmountApplied);

        Assert.All(naic.Lines, AssertCasInvariants);
        Assert.All(memberPaid.Lines, AssertCasInvariants);
        Assert.All(naic.Lines, l => Assert.True(l.PlanPaidAmount <= 150m - 80m));
    }

    /// <summary>
    /// As the first payer the setting changes nothing: NoDeductible still
    /// applies the deductible on a primary claim.
    /// </summary>
    [Fact]
    public async Task CobDeductibleCredit_NoDeductible_AppliesDeductibleAsPrimary()
    {
        var plan = CreateTestPlan(individualDeductible: 500) with { CobDeductibleCredit = CobDeductibleCredit.NoDeductible };
        var engine = CreateEngine(plan, categoryCode: "98", existingDeductible: 400m);

        var result = await engine.CalculateAsync(CreateRequest(plan.Id, lines: ("99213", 200m, 150m, "11")));

        var line = result.Lines.Single();
        Assert.Equal(100m, line.DeductibleAmount);
        Assert.Equal(100m, line.DeductibleCreditedAmount);
        Assert.Equal(100m, Snapshot(result, AccumulatorType.IndividualDeductible).AmountApplied);
    }

    [Fact]
    public void BenefitPlanConfig_CobDeductibleCredit_DefaultsToNaicFullCredit()
    {
        Assert.Equal(CobDeductibleCredit.NaicFullCredit, new BenefitPlanConfig().CobDeductibleCredit);
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
        IReadOnlyList<string> modifiers, string? revenueCode, ServiceCategoryClaimContext? claim, CancellationToken ct)
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
        IReadOnlyList<string> modifiers, string? revenueCode, ServiceCategoryClaimContext? claim, CancellationToken ct)
        => Task.FromResult<ServiceCategoryMatch?>(null);
}
