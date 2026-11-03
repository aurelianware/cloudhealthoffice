using CloudHealthOffice.BenefitEngine.Domain;
using CloudHealthOffice.BenefitEngine.Models;
using CloudHealthOffice.BenefitEngine.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CloudHealthOffice.BenefitEngine.Tests;

/// <summary>
/// Shadow adjudication hands the engine what the other payer says the member
/// and family had already met (<see cref="BenefitResolutionRequest.PriorAccumulation"/>).
/// The engine prices from it instead of the store and never writes or prepares a commit.
/// </summary>
public class PriorAccumulationTests
{
    private static BenefitPlanConfig Plan(string? planYear = null) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = "test-tenant",
        PlanName = "Test Plan",
        PlanType = PlanType.PPO,
        PlanYear = planYear,
        IndividualDeductible = 500,
        FamilyDeductible = 1000,
        IndividualOopMax = 3000,
        FamilyOopMax = 6000,
        Categories =
        [
            new BenefitCategoryConfig
            {
                ServiceTypeCode = "98",
                ServiceTypeDescription = "Office Visit",
                IsCovered = true,
                InNetworkCostSharing =
                [
                    new CostShareRuleConfig { CostShareType = CostShareType.Deductible, DeductibleApplies = true },
                    new CostShareRuleConfig { CostShareType = CostShareType.Coinsurance, CoinsurancePercent = 0.20m },
                ]
            },
        ]
    };

    private static BenefitResolutionRequest Request(
        BenefitPlanConfig plan, PriorAccumulation? prior,
        AdjudicationExecutionMode mode = AdjudicationExecutionMode.Prospective) => new()
    {
        MemberId = "MBR-001",
        SubscriberId = "SUB-001",
        BenefitPlanId = plan.Id,
        ServiceDate = new DateOnly(2026, 6, 1),
        NetworkTier = NetworkTier.InNetwork,
        ClaimId = "claim-1",
        ExecutionMode = mode,
        PriorAccumulation = prior,
        Lines = [new ClaimLineInput { LineNumber = 1, ProcedureCode = "99213", PlaceOfService = "11", BilledAmount = 200m, Units = 1 }],
        AllowedAmounts = new Dictionary<int, decimal> { [1] = 200m },
    };

    private static BenefitCalculationEngine Engine(BenefitPlanConfig plan, IAccumulatorService acc) => new(
        new FixedCategoryResolver("98", "Office Visit"),
        new InMemoryBenefitPlanProvider(plan),
        acc,
        new BenefitRuleGate(NullLogger<BenefitRuleGate>.Instance),
        NullLogger<BenefitCalculationEngine>.Instance);

    private static PriorAccumulation Prior(params PriorAccumulationEntry[] entries) => new() { Entries = [.. entries] };

    private static PriorAccumulationEntry Met(decimal deductible, bool thisMember = true, int month = 3, int year = 2026) => new()
    {
        ServiceDate = new DateOnly(year, month, 1),
        ThisMember = thisMember,
        NetworkTier = NetworkTier.InNetwork,
        Deductible = deductible,
        OutOfPocket = deductible,
    };

    [Fact]
    public async Task Without_prior_accumulation_the_member_owes_the_deductible()
    {
        var plan = Plan();
        var result = await Engine(plan, new RecordingAccumulatorService()).CalculateAsync(Request(plan, null));

        Assert.Equal(200m, result.Lines.Single().DeductibleAmount);
    }

    [Fact]
    public async Task The_members_own_earlier_deductible_counts()
    {
        var plan = Plan();
        var result = await Engine(plan, new RecordingAccumulatorService())
            .CalculateAsync(Request(plan, Prior(Met(400m))));

        // $100 of deductible left, then 20% of the remaining $100.
        Assert.Equal(100m, result.Lines.Single().DeductibleAmount);
        Assert.Equal(20m, result.Lines.Single().CoinsuranceAmount);
    }

    [Fact]
    public async Task The_familys_deductible_counts_toward_the_family_limit_only()
    {
        var plan = Plan();
        var engine = Engine(plan, new RecordingAccumulatorService());

        // Another member met $450: the family has $550 left, the member $500, so $200 still applies.
        var some = await engine.CalculateAsync(Request(plan, Prior(Met(450m, thisMember: false))));
        Assert.Equal(200m, some.Lines.Single().DeductibleAmount);

        // The family met its $1,000: nothing left for anyone.
        var met = await engine.CalculateAsync(Request(plan, Prior(Met(600m, thisMember: false), Met(400m))));
        Assert.Equal(0m, met.Lines.Single().DeductibleAmount);
    }

    [Fact]
    public async Task Amounts_from_another_plan_year_are_ignored()
    {
        var plan = Plan();
        var result = await Engine(plan, new RecordingAccumulatorService())
            .CalculateAsync(Request(plan, Prior(Met(500m, month: 12, year: 2025))));

        Assert.Equal(200m, result.Lines.Single().DeductibleAmount);
    }

    [Fact]
    public async Task Prior_accumulation_replaces_the_store_and_is_never_written_or_committed()
    {
        var plan = Plan();
        var store = new SeededAccumulatorService(500m);
        var engine = Engine(plan, store);

        var production = await engine.CalculateAsync(Request(plan, Prior(), AdjudicationExecutionMode.Production));
        var prospective = await engine.CalculateAsync(Request(plan, Prior()));

        // The store says the deductible is met; the supplied (empty) accumulation says it is not.
        Assert.Equal(200m, prospective.Lines.Single().DeductibleAmount);
        Assert.Equal(0, store.Reads);
        Assert.Equal(0, store.Writes);
        Assert.Null(production.PreparedAccumulatorCommit);
        Assert.Null(prospective.PreparedAccumulatorCommit);
    }

    private sealed class SeededAccumulatorService(decimal deductibleMet) : IAccumulatorService
    {
        public int Reads { get; private set; }
        public int Writes { get; private set; }

        public Task<IReadOnlyList<AccumulatorSnapshot>> GetAccumulatorsAsync(
            string memberId, string subscriberId, Guid benefitPlanId, string planYear, CancellationToken ct = default)
        {
            Reads++;
            return Task.FromResult<IReadOnlyList<AccumulatorSnapshot>>(
            [
                new AccumulatorSnapshot
                {
                    Type = AccumulatorType.IndividualDeductible, Scope = AccumulatorScope.Individual,
                    NetworkTier = NetworkTier.InNetwork, LimitAmount = 500m, AccumulatedAmountAfter = deductibleMet,
                },
            ]);
        }

        public Task ApplyUpdatesAsync(string memberId, string subscriberId, Guid benefitPlanId, string planYear,
            string claimId, IReadOnlyList<AccumulatorUpdate> updates, CancellationToken ct = default)
        {
            Writes++;
            return Task.CompletedTask;
        }

        public Task ReverseAsync(string memberId, string subscriberId, Guid benefitPlanId, string planYear,
            string claimId, CancellationToken ct = default) => Task.CompletedTask;

        public Task ResetForPlanYearAsync(Guid benefitPlanId, string planYear, CancellationToken ct = default)
            => Task.CompletedTask;
    }
}
