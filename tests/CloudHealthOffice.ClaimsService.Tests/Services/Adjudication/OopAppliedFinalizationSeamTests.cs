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
/// <see cref="ClaimEventPublisher"/> finalized event →
/// accumulator-service OOP delta. Cost share the plan excludes from the OOP
/// max (<see cref="CostShareRuleConfig.OopApplies"/> = false) is still owed
/// by the member but must not reach the event-driven OOP accumulator.
/// </summary>
public class OopAppliedFinalizationSeamTests
{
    private const string OfficeVisit = "98";

    [Fact]
    public async Task CoinsuranceExcludedFromOop_OwedByMember_ButNotInOopDelta()
    {
        // Allowed 100: deductible 50 (counts toward OOP) + 20% coinsurance on
        // the remaining 50 = 10 (excluded from OOP).
        var (claim, evt) = await AdjudicateAndFinalizeAsync(coinsuranceOopApplies: false);

        claim.AdjudicationResult!.PatientResponsibility.Should().Be(60m);
        claim.AdjudicationResult.OopAppliedAmount.Should().Be(50m);
        claim.ClaimLines.Single().AdjudicationResult!.OopAppliedAmount.Should().Be(50m);

        evt.MemberResponsibility.Should().Be(60m);
        evt.OopApplied.Should().Be(50m);
        evt.LineItems.Single().MemberResponsibility.Should().Be(60m);
        evt.LineItems.Single().OopApplied.Should().Be(50m);

        // Line CAS carries the cost share, so the deductible delta comes
        // through as well; the excluded coinsurance stays out of OOP only.
        evt.LineItems.Single().DeductibleApplied.Should().Be(50m);
        evt.LineItems.Single().CoinsuranceApplied.Should().Be(10m);
        var (deductible, oop, _) = AccumulatorDomainService.ComputeDeltas(evt);
        deductible.Should().Be(50m);
        oop.Should().Be(50m);
    }

    [Fact]
    public async Task AllCostShareCountsTowardOop_OopDeltaEqualsMemberResponsibility()
    {
        var (_, evt) = await AdjudicateAndFinalizeAsync(coinsuranceOopApplies: true);

        evt.LineItems.Single().OopApplied.Should().Be(60m);
        var (_, oop, _) = AccumulatorDomainService.ComputeDeltas(evt);
        oop.Should().Be(60m);
    }

    [Fact]
    public void LegacyClaimWithoutOopAppliedAmount_FallsBackToPatientResponsibility()
    {
        // Claims persisted before OopAppliedAmount existed deserialize it as
        // null; their OOP contribution stays the full patient responsibility,
        // which is what they were adjudicated with.
        var claim = new Claim
        {
            Id = "legacy-1",
            ClaimNumber = "LEGACY-1",
            MemberId = "MEM-1",
            ServiceDateFrom = new DateTime(2026, 4, 15),
            Status = ClaimStatus.Paid,
            AdjudicationResult = new AdjudicationResult { PatientResponsibility = 40m },
            ClaimLines =
            {
                new ClaimLine
                {
                    LineNumber = 1,
                    ProcedureCode = "99213",
                    AdjudicationResult = new LineAdjudicationResult { PatientResponsibility = 40m },
                },
            },
        };

        var evt = ClaimEventPublisher.BuildFinalizedEvent(claim, "tenant-1");

        evt.OopApplied.Should().Be(40m);
        evt.LineItems.Single().OopApplied.Should().Be(40m);
        AccumulatorDomainService.ComputeDeltas(evt).oop.Should().Be(40m);
    }

    private static async Task<(Claim Claim, ClaimFinalizedEvent Event)> AdjudicateAndFinalizeAsync(
        bool coinsuranceOopApplies)
    {
        var plan = new BenefitPlanConfig
        {
            Id = Guid.NewGuid(),
            TenantId = "tenant-1",
            PlanName = "Seam PPO",
            PlanType = CloudHealthOffice.BenefitEngine.Domain.PlanType.PPO,
            PlanYear = "2026",
            IndividualDeductible = 50m,
            IndividualOopMax = 3_000m,
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
                            CostShareType = CostShareType.Coinsurance,
                            CoinsurancePercent = 0.20m,
                            DeductibleApplies = true,
                            OopApplies = coinsuranceOopApplies,
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
            Id = "claim-oop",
            ClaimVersionId = "claim-oop",
            ClaimNumber = "CLM-OOP",
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
                new() { LineNumber = 1, ProcedureCode = "99213", ChargeAmount = 100m, Units = 1,
                        ServiceDateFrom = serviceDate, ServiceDateTo = serviceDate },
            },
        };
        var ctx = new ClaimAdjudicationContext
        {
            TenantId = "tenant-1",
            ClaimVersionId = adapterClaim.Id,
            Claim = adapterClaim,
            // BenefitCalculationStage fails closed without fee-schedule
            // allowed amounts (#1235); price at billed for this seam.
            PricingResult = new PricingOutcome
            {
                AllowedAmounts = adapterClaim.ClaimLines.ToDictionary(l => l.LineNumber, l => l.ChargeAmount),
            },
            ResolvedMember = new ResolvedMember { MemberId = "MEM-1", IsSubscriber = true },
            // PricingStage (Order 250) supplies allowed amounts; the benefit
            // stage pends without them.
            PricingResult = new PricingOutcome
            {
                AllowedAmounts = adapterClaim.ClaimLines.ToDictionary(l => l.LineNumber, _ => 100m),
            },
        };

        var stageResult = await stage.ExecuteAsync(ctx, CancellationToken.None);
        stageResult.Outcome.Should().Be(ClaimAdjudicationOutcome.Pass);

        // What PersistenceStage writes: claim-level result plus one line
        // result per claim line, in order.
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
                    AdjudicationResult = ctx.LineAdjudicationResults[i],
                })
                .ToList(),
        };

        return (claim, ClaimEventPublisher.BuildFinalizedEvent(claim, "tenant-1"));
    }
}
