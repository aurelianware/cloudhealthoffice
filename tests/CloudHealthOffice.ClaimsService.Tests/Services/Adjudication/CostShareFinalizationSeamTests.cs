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
        AdjudicateAndFinalizeAsync(decimal oopMax)
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
                Arg.Any<string?>(), Arg.Any<CancellationToken>())
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

        var stage = new BenefitCalculationStage(
            engine,
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
}
