extern alias accumulator;

using ClaimsService.Models;
using ClaimsService.Models.Adjudication;
using ClaimsService.Services;
using ClaimsService.Services.Adjudication;
using ClaimsService.Services.Adjudication.Stages;
using ClaimsService.Services.Resolution;
using CloudHealthOffice.BenefitEngine.Domain;
using CloudHealthOffice.BenefitEngine.Services;
using CloudHealthOffice.Events;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using AccumulatorDomainService = accumulator::AccumulatorService.Services.AccumulatorService;
using EngineAccumulatorService = CloudHealthOffice.BenefitEngine.Services.IAccumulatorService;

namespace CloudHealthOffice.ClaimsService.Tests.Services.Adjudication;

/// <summary>
/// Seam test across services: real <see cref="BenefitCalculationEngine"/>
/// → <see cref="BenefitCalculationStage"/> → persisted claim →
/// <see cref="ClaimEventPublisher"/> finalized event → accumulator-service
/// <c>ComputeDeltas</c>. The finalized event derives each line's
/// deductible / copay / coinsurance from the line's CAS
/// (<see cref="LineAdjudicationResult.AdjustmentReasons"/>, PR-1/PR-3/PR-2),
/// and accumulator-service prefers those line amounts — so the stage must
/// carry the engine's per-line adjustments onto the claim line, or the
/// event-driven deductible accumulator never moves.
/// </summary>
public class CostShareFinalizationSeamTests
{
    private const string OfficeVisit = "98";

    // Plan: $120 deductible, $20 copay in addition to deductible, 20%
    // coinsurance. Two lines billed 150 / 200, each priced at 100.
    //   Line 1: deductible 100 → member 100, plan 0.
    //   Line 2: deductible 20, copay 20, coinsurance 20% × 60 = 12
    //           → member 52, plan 48.
    [Fact]
    public async Task EngineCostShare_ReachesAccumulatorDeltas()
    {
        var (ctx, claim, evt) = await AdjudicateAndFinalizeAsync(oopMax: 3_000m);
        var engine = ctx.BenefitResolutionResult!;

        engine.Totals.TotalDeductible.Should().Be(120m);
        engine.Totals.TotalCopay.Should().Be(20m);
        engine.Totals.TotalCoinsurance.Should().Be(12m);
        engine.Totals.TotalMemberResponsibility.Should().Be(152m);

        evt.LineItems.Select(l => l.DeductibleApplied).Should().Equal(100m, 20m);
        evt.LineItems.Select(l => l.CopayApplied).Should().Equal(0m, 20m);
        evt.LineItems.Select(l => l.CoinsuranceApplied).Should().Equal(0m, 12m);
        evt.LineItems.Select(l => l.OopApplied).Should().Equal(100m, 52m);

        var (deductible, oop, services) = AccumulatorDomainService.ComputeDeltas(evt);
        deductible.Should().Be(engine.Totals.TotalDeductible);
        oop.Should().Be(engine.Totals.TotalMemberResponsibility);
        services.Sum(s => s.UsedDelta).Should().Be(
            engine.Totals.TotalDeductible + engine.Totals.TotalCopay + engine.Totals.TotalCoinsurance);

        AssertClaimTotalsMatchLineCas(claim);
        AssertLinesBalance(ctx, claim);
    }

    // OOP max 130: line 1 consumes 100, line 2's raw 52 (deductible 20,
    // copay 20, coinsurance 12) caps at 30. The cap forgives coinsurance
    // first, then copay: PR-1 20 / PR-3 10, no PR-2 and no OA-23.
    [Fact]
    public async Task OopMaxReached_DeductibleStillCounted_OopDeltaCapped()
    {
        var (ctx, claim, evt) = await AdjudicateAndFinalizeAsync(oopMax: 130m);
        var engine = ctx.BenefitResolutionResult!;

        engine.Totals.TotalOopMaxReduction.Should().Be(22m);
        engine.Totals.TotalMemberResponsibility.Should().Be(130m);
        engine.Totals.TotalDeductible.Should().Be(120m);
        engine.Totals.TotalCopay.Should().Be(10m);
        engine.Totals.TotalCoinsurance.Should().Be(0m);

        var line2 = claim.ClaimLines[1].AdjudicationResult!;
        line2.AdjustmentReasons.Select(r => (r.GroupCode, r.ReasonCode, r.Amount)).Should().Equal(
            ("CO", "45", 100m), ("PR", "1", 20m), ("PR", "3", 10m));
        claim.ClaimLines.SelectMany(l => l.AdjudicationResult!.AdjustmentReasons)
            .Should().OnlyContain(r => r.Amount > 0m);

        // Per line the finalized cost share equals member responsibility and
        // the OOP-applied amount.
        evt.LineItems.Select(l => l.DeductibleApplied).Should().Equal(100m, 20m);
        evt.LineItems.Select(l => l.CopayApplied).Should().Equal(0m, 10m);
        evt.LineItems.Select(l => l.CoinsuranceApplied).Should().Equal(0m, 0m);
        evt.LineItems.Should().OnlyContain(l =>
            l.DeductibleApplied + l.CopayApplied + l.CoinsuranceApplied == l.MemberResponsibility
            && l.OopApplied == l.MemberResponsibility);

        var (deductible, oop, services) = AccumulatorDomainService.ComputeDeltas(evt);
        deductible.Should().Be(engine.Totals.TotalDeductible).And.Be(120m);
        oop.Should().Be(130m);
        services.Sum(s => s.UsedDelta).Should().Be(130m);

        AssertClaimTotalsMatchLineCas(claim);
        AssertLinesBalance(ctx, claim);
    }

    // Secondary payer (complementary COB). Line 1: no primary payment, so
    // COB changes nothing (deductible 100, plan 0). Line 2: pre-COB the
    // member owes 52 (deductible 20, copay 20, coinsurance 12) and the plan
    // pays 48; the primary paid 30, so the plan still pays 48 and the member
    // owes 100 − 30 − 48 = 22. PR shrinks coinsurance-first to PR-1 20 /
    // PR-3 2, the positive OA-23 is 30, and the finalized event and the
    // accumulator deltas carry the reduced amounts.
    [Fact]
    public async Task SecondaryPayerCob_PrEqualsMemberLiability_ReachesAccumulatorDeltas()
    {
        var (ctx, claim, evt) = await AdjudicateAndFinalizeAsync(
            oopMax: 3_000m,
            cob: new CloudHealthOffice.BenefitEngine.Models.CobInfo
            {
                PayerSequence = 2,
                UseComplementaryModel = true,
                PrimaryPayerPaymentByLine = new() { [2] = 30m },
            });
        var engine = ctx.BenefitResolutionResult!;

        engine.Totals.TotalDeductible.Should().Be(120m);
        engine.Totals.TotalCopay.Should().Be(2m);
        engine.Totals.TotalCoinsurance.Should().Be(0m);
        engine.Totals.TotalMemberResponsibility.Should().Be(122m);
        engine.Totals.TotalPlanPaid.Should().Be(48m);

        var line2 = claim.ClaimLines[1].AdjudicationResult!;
        line2.AdjustmentReasons.Select(r => (r.GroupCode, r.ReasonCode, r.Amount)).Should().Equal(
            ("CO", "45", 100m), ("PR", "1", 20m), ("PR", "3", 2m), ("OA", "23", 30m));
        claim.ClaimLines.SelectMany(l => l.AdjudicationResult!.AdjustmentReasons)
            .Should().OnlyContain(r => r.Amount > 0m);
        claim.ClaimLines.Should().OnlyContain(l =>
            l.AdjudicationResult!.AdjustmentReasons.Where(r => r.GroupCode == "PR").Sum(r => r.Amount)
                == l.AdjudicationResult.PatientResponsibility);

        evt.LineItems.Select(l => l.DeductibleApplied).Should().Equal(100m, 20m);
        evt.LineItems.Select(l => l.CopayApplied).Should().Equal(0m, 2m);
        evt.LineItems.Select(l => l.CoinsuranceApplied).Should().Equal(0m, 0m);
        evt.LineItems.Select(l => l.OopApplied).Should().Equal(100m, 22m);
        evt.LineItems.Select(l => l.MemberResponsibility).Should().Equal(100m, 22m);

        var (deductible, oop, services) = AccumulatorDomainService.ComputeDeltas(evt);
        deductible.Should().Be(120m);
        oop.Should().Be(122m);
        services.Sum(s => s.UsedDelta).Should().Be(122m);

        AssertClaimTotalsMatchLineCas(claim);
        AssertLinesBalance(ctx, claim);
    }

    // Tertiary payer, from the claim's own 837 payer data (no injected
    // CobInfo): 2000B SBR01 "T", primary and secondary in OtherPayers with
    // 2430 line adjudication; CoordinationOfBenefitsStage cleared COB, so
    // BenefitCalculationStage.BuildCob sends both prior payers to the engine.
    // Priced first as the only plan: L1 deductible 100 → cost share 100,
    // normal 0; L2 deductible 20, copay 20, coinsurance 20% × 60 = 12 →
    // cost share 52, normal 48.
    // Prior paid per line 40 + 30 = 70 and 50 + 20 = 70 (room 30 + 30); the
    // secondary adjudicated both lines and left the member PR 30 + 30 = 60 →
    // claim balance min(60, 60) = 60. Claim-level COB: pay min(48, 60) = 48;
    // member min(152, 60 − 48) = 12.
    // Lines: the payment by normal benefit, within each line's balance (30):
    // L2 30, then the 18 left to L1; the member's 12 on L1 (PR-1 12).
    // L1: CO-45 50, PR-1 12, OA-23 = 100 − 12 − 18 = 70. L2: CO-45 100,
    // OA-23 = 100 − 30 = 70. The 835 is the same in both modes.
    // Deductible credit: NAIC full (default) credits the 100 + 20 the plan
    // applied alone (= the 120 deductible, met); member-paid only the PR-1
    // 12. The OOP and service rollups take the member's 12 either way.
    [Theory]
    [InlineData(CobDeductibleCredit.NaicFullCredit, 120)]
    [InlineData(CobDeductibleCredit.MemberPaidOnly, 12)]
    public async Task TertiaryPayerCob_FromClaimPayerData_ReachesAccumulatorDeltas(
        CobDeductibleCredit credit, decimal expectedDeductibleDelta)
    {
        static ClaimAdjustmentReason Cas(string group, string carc, decimal amount) =>
            new() { GroupCode = group, ReasonCode = carc, Amount = amount };

        var (ctx, claim, evt) = await AdjudicateAndFinalizeAsync(
            oopMax: 3_000m,
            deductibleCredit: credit,
            configureClaim: c =>
            {
                c.PayerResponsibilityCode = "T";
                c.OtherPayers =
                [
                    new ClaimOtherPayer
                    {
                        PayerResponsibilityCode = "P", PayerId = "PRIM", PaidAmount = 90m,
                        LineAdjudications =
                        [
                            new() { LineNumber = 1, PaidAmount = 40m, Adjustments = [Cas("CO", "45", 50m), Cas("PR", "1", 60m)] },
                            new() { LineNumber = 2, PaidAmount = 50m, Adjustments = [Cas("CO", "45", 100m), Cas("PR", "2", 50m)] },
                        ],
                    },
                    new ClaimOtherPayer
                    {
                        PayerResponsibilityCode = "S", PayerId = "SEC", PaidAmount = 50m,
                        LineAdjudications =
                        [
                            new() { LineNumber = 1, PaidAmount = 30m, Adjustments = [Cas("OA", "23", 90m), Cas("PR", "1", 30m)] },
                            new() { LineNumber = 2, PaidAmount = 20m, Adjustments = [Cas("OA", "23", 150m), Cas("PR", "2", 30m)] },
                        ],
                    },
                ];
            });
        var engine = ctx.BenefitResolutionResult!;

        engine.Totals.TotalPlanPaid.Should().Be(48m);
        engine.Totals.TotalMemberResponsibility.Should().Be(12m);
        engine.Totals.TotalDeductible.Should().Be(12m);
        engine.Totals.TotalDeductibleCredited.Should().Be(expectedDeductibleDelta);
        ctx.AdjudicationResult!.DeductibleCreditedAmount.Should().Be(expectedDeductibleDelta);
        ctx.AdjudicationResult.CobPayerSequence.Should().Be(3);

        claim.ClaimLines[0].AdjudicationResult!.AdjustmentReasons
            .Select(r => (r.GroupCode, r.ReasonCode, r.Amount)).Should().Equal(
                ("CO", "45", 50m), ("PR", "1", 12m), ("OA", "23", 70m));
        claim.ClaimLines[1].AdjudicationResult!.AdjustmentReasons
            .Select(r => (r.GroupCode, r.ReasonCode, r.Amount)).Should().Equal(
                ("CO", "45", 100m), ("OA", "23", 70m));
        claim.ClaimLines.Select(l => l.AdjudicationResult!.PaidAmount).Should().Equal(18m, 30m);

        evt.LineItems.Select(l => l.DeductibleApplied).Should().Equal(12m, 0m);
        var (deductible, oop, services) = AccumulatorDomainService.ComputeDeltas(evt);
        deductible.Should().Be(expectedDeductibleDelta);
        oop.Should().Be(12m);
        services.Sum(s => s.UsedDelta).Should().Be(12m);

        AssertClaimTotalsMatchLineCas(claim);
        AssertLinesBalance(ctx, claim);
    }

    // accumulator-service: the credited deductible goes to the deductible
    // delta (line items, or claim level when there are none); the service
    // rollup and OOP keep the member's cost share. Null = DeductibleApplied.
    [Fact]
    public void ComputeDeltas_UsesDeductibleCredited_WhenPresent()
    {
        var lines = new ClaimFinalizedEvent
        {
            BenefitCategory = "OV",
            LineItems =
            {
                new ClaimFinalizedLineItem { BenefitCategory = "OV", DeductibleApplied = 30m, DeductibleCredited = 100m, OopApplied = 30m },
                new ClaimFinalizedLineItem { BenefitCategory = "OV", DeductibleApplied = 20m, OopApplied = 20m },
            },
        };
        var (deductible, oop, services) = AccumulatorDomainService.ComputeDeltas(lines);
        deductible.Should().Be(120m);
        oop.Should().Be(50m);
        services.Sum(s => s.UsedDelta).Should().Be(50m);

        var claimLevel = new ClaimFinalizedEvent
        {
            BenefitCategory = "OV", DeductibleApplied = 30m, DeductibleCredited = 100m, OopApplied = 30m,
        };
        var (claimDeductible, claimOop, claimServices) = AccumulatorDomainService.ComputeDeltas(claimLevel);
        claimDeductible.Should().Be(100m);
        claimOop.Should().Be(30m);
        claimServices.Sum(s => s.UsedDelta).Should().Be(30m);
    }

    [Fact]
    public void FinalizedEvent_CountsOnlyPatientResponsibilityGroupAsCostShare()
    {
        // A CO entry reusing CARC 1/2/3 is a payer write-off, not member
        // deductible/coinsurance/copay.
        var claim = new Claim
        {
            Id = "c-1",
            ClaimNumber = "C-1",
            MemberId = "MEM-1",
            ServiceDateFrom = new DateTime(2026, 4, 15),
            Status = ClaimStatus.Paid,
            AdjudicationResult = new AdjudicationResult { DeductibleAmount = 30m, PatientResponsibility = 30m },
            ClaimLines =
            {
                new ClaimLine
                {
                    LineNumber = 1,
                    ProcedureCode = "99213",
                    AdjudicationResult = new LineAdjudicationResult
                    {
                        PatientResponsibility = 30m,
                        AdjustmentReasons =
                        {
                            new ClaimAdjustmentReason { GroupCode = "PR", ReasonCode = "1", Amount = 30m },
                            new ClaimAdjustmentReason { GroupCode = "CO", ReasonCode = "1", Amount = 5m },
                            new ClaimAdjustmentReason { GroupCode = "CO", ReasonCode = "2", Amount = 7m },
                            new ClaimAdjustmentReason { GroupCode = "OA", ReasonCode = "3", Amount = 9m },
                        },
                    },
                },
            },
        };

        var line = ClaimEventPublisher.BuildFinalizedEvent(claim, "tenant-1").LineItems.Single();

        line.DeductibleApplied.Should().Be(30m);
        line.CoinsuranceApplied.Should().Be(0m);
        line.CopayApplied.Should().Be(0m);
    }

    private static void AssertClaimTotalsMatchLineCas(Claim claim)
    {
        var lineReasons = claim.ClaimLines.SelectMany(l => l.AdjudicationResult!.AdjustmentReasons).ToList();
        decimal Sum(string carc) => lineReasons
            .Where(r => r.GroupCode == "PR" && r.ReasonCode == carc)
            .Sum(r => r.Amount);

        var adj = claim.AdjudicationResult!;
        adj.DeductibleAmount.Should().Be(Sum("1"));
        adj.CoinsuranceAmount.Should().Be(Sum("2"));
        adj.CopayAmount.Should().Be(Sum("3"));
        adj.PatientResponsibility.Should().Be(claim.ClaimLines.Sum(l => l.AdjudicationResult!.PatientResponsibility));
        adj.PayerPayment.Should().Be(claim.ClaimLines.Sum(l => l.AdjudicationResult!.PaidAmount));
    }

    /// <summary>835 SVC invariant: SVC02 (charge) − ΣCAS = SVC03 (paid).</summary>
    private static void AssertLinesBalance(ClaimAdjudicationContext ctx, Claim claim)
    {
        foreach (var line in claim.ClaimLines)
        {
            var result = line.AdjudicationResult!;
            (line.ChargeAmount - result.AdjustmentReasons.Sum(r => r.Amount))
                .Should().Be(result.PaidAmount, $"line {line.LineNumber} CAS must balance");
            result.AdjustmentReasons.Should().ContainEquivalentOf(
                new { GroupCode = "CO", ReasonCode = "45", Amount = line.ChargeAmount - result.AllowedAmount });
        }
        ctx.LineAdjudicationResults.Should().HaveCount(claim.ClaimLines.Count);
    }

    private static async Task<(ClaimAdjudicationContext Ctx, Claim Claim, ClaimFinalizedEvent Event)>
        AdjudicateAndFinalizeAsync(
            decimal oopMax, CloudHealthOffice.BenefitEngine.Models.CobInfo? cob = null,
            Action<AdapterClaim>? configureClaim = null,
            CobDeductibleCredit deductibleCredit = CobDeductibleCredit.NaicFullCredit)
    {
        var plan = new BenefitPlanConfig
        {
            Id = Guid.NewGuid(),
            TenantId = "tenant-1",
            PlanName = "Seam PPO",
            PlanType = CloudHealthOffice.BenefitEngine.Domain.PlanType.PPO,
            PlanYear = "2026",
            IndividualDeductible = 120m,
            FamilyDeductible = 360m,
            IndividualOopMax = oopMax,
            FamilyOopMax = oopMax * 3,
            CobDeductibleCredit = deductibleCredit,
            Categories =
            [
                new BenefitCategoryConfig
                {
                    ServiceTypeCode = OfficeVisit,
                    ServiceTypeDescription = "Office Visit",
                    IsCovered = true,
                    InNetworkCostSharing =
                    [
                        new CostShareRuleConfig { CostShareType = CostShareType.Deductible, DeductibleApplies = true },
                        new CostShareRuleConfig
                        {
                            CostShareType = CostShareType.Copay,
                            CopayAmount = 20m,
                            CopayApplicationMode = CopayApplicationMode.InAdditionToDeductible,
                        },
                        new CostShareRuleConfig
                        {
                            CostShareType = CostShareType.Coinsurance,
                            CoinsurancePercent = 0.20m,
                            DeductibleApplies = true,
                        },
                    ],
                },
            ],
        };

        var planProvider = Substitute.For<IBenefitPlanProvider>();
        planProvider.GetPlanAsync(plan.Id, Arg.Any<CancellationToken>()).Returns(plan);

        var categoryResolver = Substitute.For<IServiceCategoryResolver>();
        categoryResolver.ResolveAsync(
                Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<DateOnly>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(),
                Arg.Any<string?>(), Arg.Any<ServiceCategoryClaimContext?>(), Arg.Any<CancellationToken>())
            .Returns(new ServiceCategoryMatch
            {
                ServiceTypeCode = OfficeVisit,
                ServiceTypeDescription = "Office Visit",
                MatchedBy = "Test",
                MatchedRule = "Test",
            });

        var engineAccumulators = Substitute.For<EngineAccumulatorService>();
        engineAccumulators.GetAccumulatorsAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(Array.Empty<AccumulatorSnapshot>());

        var engine = new BenefitCalculationEngine(
            categoryResolver, planProvider, engineAccumulators,
            new BenefitRuleGate(NullLogger<BenefitRuleGate>.Instance),
            NullLogger<BenefitCalculationEngine>.Instance);

        // BenefitCalculationStage builds Cob from the claim's 837 payer data
        // (configureClaim); a test can instead inject a CobInfo on the way
        // into the real engine (the legacy primary-payment-by-line input).
        IBenefitCalculationEngine stageEngine = cob is null ? engine : new WithCob(engine, cob);

        var stage = new BenefitCalculationStage(
            stageEngine,
            Substitute.For<IMemberResolver>(),
            Substitute.For<IAuthorizationValidationClient>(),
            NullLogger<BenefitCalculationStage>.Instance);

        var serviceDate = new DateTime(2026, 4, 15, 0, 0, 0, DateTimeKind.Utc);
        var adapterClaim = new AdapterClaim
        {
            TenantId = "tenant-1",
            Id = "claim-cost-share",
            ClaimVersionId = "claim-cost-share",
            ClaimNumber = "CLM-CS",
            MemberId = "MEM-1",
            SubscriberId = "MEM-1",
            BenefitPlanId = plan.Id.ToString(),
            BillingProviderNPI = "1234567890",
            LineOfBusiness = LineOfBusiness.Commercial,
            ClaimType = ClaimType.Professional,
            PlaceOfServiceCode = "11",
            ServiceDateFrom = serviceDate,
            ServiceDateTo = serviceDate,
            ClaimLines = new List<AdapterClaimLine>
            {
                new() { LineNumber = 1, ProcedureCode = "99214", ChargeAmount = 150m, Units = 1,
                        ServiceDateFrom = serviceDate, ServiceDateTo = serviceDate },
                new() { LineNumber = 2, ProcedureCode = "99213", ChargeAmount = 200m, Units = 1,
                        ServiceDateFrom = serviceDate, ServiceDateTo = serviceDate },
            },
        };
        configureClaim?.Invoke(adapterClaim);
        var ctx = new ClaimAdjudicationContext
        {
            TenantId = "tenant-1",
            ClaimVersionId = adapterClaim.Id,
            Claim = adapterClaim,
            PricingResult = new PricingOutcome
            {
                AllowedAmounts = adapterClaim.ClaimLines.ToDictionary(l => l.LineNumber, _ => 100m),
            },
            ResolvedMember = new ResolvedMember { MemberId = "MEM-1", IsSubscriber = true },
            // What CoordinationOfBenefitsStage (Order 275) leaves when
            // coverage-service and the 837 agree on a later payer sequence
            // and the 837 carries complete prior-payer data.
            CobResult = CloudHealthOffice.CobEngine.Domain.PayerResponsibility.ToSequence(adapterClaim.PayerResponsibilityCode) is >= 2 and var seq
                ? new CobOutcome
                {
                    Scenario = seq == 2
                        ? CobScenario.ChoSecondaryDetected
                        : CobScenario.ChoTertiaryDetected,
                    ApplyCob = true,
                    PayerSequence = seq,
                }
                : null,
        };

        var stageResult = await stage.ExecuteAsync(ctx, CancellationToken.None);
        stageResult.Outcome.Should().Be(ClaimAdjudicationOutcome.Pass);

        // What PersistenceStage writes: claim-level result plus one line
        // result per claim line, positionally.
        var claim = new Claim
        {
            Id = adapterClaim.Id,
            ClaimNumber = adapterClaim.ClaimNumber,
            MemberId = adapterClaim.MemberId,
            ServiceDateFrom = serviceDate,
            PlaceOfServiceCode = "11",
            Status = ClaimStatus.Paid,
            AdjudicationResult = ctx.AdjudicationResult,
            ClaimLines = adapterClaim.ClaimLines
                .Select((l, i) => new ClaimLine
                {
                    LineNumber = l.LineNumber,
                    ProcedureCode = l.ProcedureCode,
                    ChargeAmount = l.ChargeAmount,
                    AdjudicationResult = ctx.LineAdjudicationResults[i],
                })
                .ToList(),
        };

        return (ctx, claim, ClaimEventPublisher.BuildFinalizedEvent(claim, "tenant-1"));
    }

    private sealed class WithCob(
        IBenefitCalculationEngine inner,
        CloudHealthOffice.BenefitEngine.Models.CobInfo cob) : IBenefitCalculationEngine
    {
        public Task<CloudHealthOffice.BenefitEngine.Models.BenefitResolutionResult> CalculateAsync(
            CloudHealthOffice.BenefitEngine.Models.BenefitResolutionRequest request, CancellationToken ct = default)
            => inner.CalculateAsync(request with { Cob = cob }, ct);

        public Task<CloudHealthOffice.OperatingMode.AugmentResult<CloudHealthOffice.BenefitEngine.Models.BenefitResolutionResult>>
            CalculateWithModeAsync(
                CloudHealthOffice.BenefitEngine.Models.BenefitResolutionRequest request,
                CloudHealthOffice.OperatingMode.IOperatingMode operatingMode,
                string tenantId,
                CloudHealthOffice.BenefitEngine.Models.BenefitResolutionResult? legacyResult = null,
                CancellationToken ct = default)
            => inner.CalculateWithModeAsync(request with { Cob = cob }, operatingMode, tenantId, legacyResult, ct);

        public Task ReverseClaimAsync(
            string memberId, string subscriberId, Guid benefitPlanId, DateOnly serviceDate,
            string originalClaimId, CancellationToken ct = default)
            => inner.ReverseClaimAsync(memberId, subscriberId, benefitPlanId, serviceDate, originalClaimId, ct);
    }
}
