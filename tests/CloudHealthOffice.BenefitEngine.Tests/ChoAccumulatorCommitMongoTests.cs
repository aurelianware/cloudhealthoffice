using CloudHealthOffice.BenefitEngine.Domain;
using CloudHealthOffice.BenefitEngine.Models;
using CloudHealthOffice.BenefitEngine.Persistence;
using CloudHealthOffice.BenefitEngine.Services;
using CloudHealthOffice.Testing.Mongo;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;
using Xunit;

namespace CloudHealthOffice.BenefitEngine.Tests;

/// <summary>
/// <see cref="ChoAccumulatorService"/> over <see cref="AccumulatorRepositoryMongo"/> on a real
/// mongod (EphemeralMongo), ported from the Cosmos emulator suite: the prepared commit is
/// written once (version-checked replace), replaces the claim's own earlier commit and the
/// replaced claim's,
/// clamps the deductible at write time, and a terminal reversal (void / denial) fences the
/// claim id so a commit arriving in either order leaves nothing.
/// </summary>
[Collection(MongoRunnerFixture.CollectionName)]
public sealed class ChoAccumulatorCommitMongoTests(MongoRunnerFixture mongo) : IAsyncLifetime
{
    private static readonly Guid Plan = Guid.Parse("6f1c2b0e-3d4a-4b5c-8e9f-0a1b2c3d4e5f");
    private const string Year = "2026";
    private readonly string _tenant = "t-" + Guid.NewGuid().ToString("N")[..10];
    private ChoAccumulatorService _store = null!;
    private IMongoDatabase _db = null!;

    public Task InitializeAsync()
    {
        _db = mongo.CreateDatabase("benefit_engine_accum");
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();
        _store = new ChoAccumulatorService(
            new AccumulatorRepositoryMongo(_db, config, NullLogger<AccumulatorRepositoryMongo>.Instance),
            new Tenant(_tenant), NullLogger<ChoAccumulatorService>.Instance);
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => mongo.DropDatabaseAsync(_db);

    private static AccumulatorCommit Commit(string claim, decimal deductible, string? replaces = null) => new()
    {
        CommitId = Guid.NewGuid().ToString("N"),
        ClaimId = claim,
        ReplacesClaimId = replaces,
        MemberId = "M1",
        SubscriberId = "S1",
        BenefitPlanId = Plan,
        PlanYear = Year,
        Updates =
        [
            new AccumulatorUpdate { Type = AccumulatorType.IndividualDeductible, Scope = AccumulatorScope.Individual,
                NetworkTier = NetworkTier.InNetwork, Amount = deductible, Source = "Deductible", ClampAtLimit = 500m },
            new AccumulatorUpdate { Type = AccumulatorType.FamilyDeductible, Scope = AccumulatorScope.Family,
                NetworkTier = NetworkTier.InNetwork, Amount = deductible, Source = "Deductible" },
        ],
    };

    private async Task<decimal> Read(AccumulatorType type) =>
        (await _store.GetAccumulatorsAsync("M1", "S1", Plan, Year))
        .Where(s => s.Type == type).Sum(s => s.AccumulatedAmountAfter);

    [Fact]
    public async Task Commit_IsWrittenOnce_ARepeatIsANoOp()
    {
        var commit = Commit("C1", 100m);

        Assert.Equal(AccumulatorCommitOutcome.Committed, (await _store.CommitAsync(commit)).Outcome);
        Assert.Equal(AccumulatorCommitOutcome.AlreadyCommitted, (await _store.CommitAsync(commit)).Outcome);

        Assert.Equal(100m, await Read(AccumulatorType.IndividualDeductible));
        Assert.Equal(100m, await Read(AccumulatorType.FamilyDeductible));
    }

    [Fact]
    public async Task Commit_ReplacesTheClaimsOwnAndTheReplacedClaimsWrite_AndClamps()
    {
        await _store.CommitAsync(Commit("C1", 200m));
        await _store.CommitAsync(Commit("C1", 150m));          // re-adjudication of C1
        Assert.Equal(150m, await Read(AccumulatorType.IndividualDeductible));

        await _store.CommitAsync(Commit("C2", 300m, replaces: "C1"));
        Assert.Equal(300m, await Read(AccumulatorType.IndividualDeductible));

        var c3 = await _store.CommitAsync(Commit("C3", 400m));         // only 200 left under the 500 limit
        Assert.Equal(500m, await Read(AccumulatorType.IndividualDeductible));
        Assert.Contains(c3.Clamped, c => c.Type == AccumulatorType.IndividualDeductible && c.Requested == 400m && c.Applied == 200m);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CommitAndTerminalReversal_InEitherOrder_LeaveNothing(bool commitFirst)
    {
        var commit = Commit("C1", 100m);

        if (commitFirst) Assert.Equal(AccumulatorCommitOutcome.Committed, (await _store.CommitAsync(commit)).Outcome);
        await _store.ReverseTerminallyAsync("M1", "S1", Plan, Year, "C1");
        if (!commitFirst)
            Assert.Equal(AccumulatorCommitOutcome.RefusedClaimReversed, (await _store.CommitAsync(commit)).Outcome);

        Assert.Equal(0m, await Read(AccumulatorType.IndividualDeductible));
        Assert.Equal(0m, await Read(AccumulatorType.FamilyDeductible));
        Assert.Equal(AccumulatorCommitOutcome.RefusedClaimReversed, (await _store.CommitAsync(Commit("C1", 100m))).Outcome);
    }

    /// <summary>A repeat of a clamped commit reports the first attempt's clamps (whichever attempt claims-service hears first records the review).</summary>
    [Fact]
    public async Task ARepeatOfAClampedCommit_ReportsTheClampsAgain()
    {
        await _store.CommitAsync(Commit("C0", 450m));
        var commit = Commit("C1", 100m);

        var first = await _store.CommitAsync(commit);
        var repeat = await _store.CommitAsync(commit);

        Assert.Equal(AccumulatorCommitOutcome.AlreadyCommitted, repeat.Outcome);
        var clamp = Assert.Single(first.Clamped);
        var again = Assert.Single(repeat.Clamped);
        Assert.Equal((clamp.Type, clamp.Requested, clamp.Applied, clamp.Limit), (again.Type, again.Requested, again.Applied, again.Limit));
        Assert.Equal((100m, 50m, 500m), (again.Requested, again.Applied, again.Limit));
    }

    /// <summary>The replacement fences the claim it replaces: the original's late commit is refused, not counted twice.</summary>
    [Fact]
    public async Task AReplacementsCommit_FencesTheReplacedClaim()
    {
        var original = Commit("C1", 200m);

        await _store.CommitAsync(Commit("C2", 300m, replaces: "C1"));

        Assert.Equal(AccumulatorCommitOutcome.RefusedClaimReversed, (await _store.CommitAsync(original)).Outcome);
        Assert.Equal(300m, await Read(AccumulatorType.IndividualDeductible));
        Assert.Equal(300m, await Read(AccumulatorType.FamilyDeductible));
    }

    [Fact]
    public async Task ResetForPlanYear_DeletesThePlanYearsAccumulators()
    {
        await _store.CommitAsync(Commit("C1", 100m));
        Assert.Equal(100m, await Read(AccumulatorType.IndividualDeductible));

        await _store.ResetForPlanYearAsync(Plan, Year);

        Assert.Empty(await _store.GetAccumulatorsAsync("M1", "S1", Plan, Year));
    }

    private sealed class Tenant(string id) : IBenefitEngineTenantContext
    {
        public string TenantId { get; } = id;
    }
}
