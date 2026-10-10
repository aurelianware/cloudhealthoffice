using CloudHealthOffice.BenefitEngine.Domain;
using CloudHealthOffice.BenefitEngine.Models;
using CloudHealthOffice.BenefitEngine.Persistence;
using CloudHealthOffice.BenefitEngine.Services;
using CloudHealthOffice.Testing.Cosmos;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CloudHealthOffice.BenefitEngine.Tests;

/// <summary>
/// <see cref="ChoAccumulatorService"/> over <see cref="AccumulatorRepositoryCosmos"/> on the
/// Cosmos DB emulator — the Cosmos twin of the Mongo commit / fence tests
/// (GoldenPath <c>AccumulatorIntegrityTests</c>): the prepared commit is written once
/// (ETag-checked replace), replaces the claim's own earlier commit and the replaced claim's,
/// clamps the deductible at write time, and a terminal reversal (void / denial) fences the
/// claim id so a commit arriving in either order leaves nothing.
/// </summary>
[Trait("Category", CosmosEmulator.Category)]
[Collection(CosmosEmulatorFixture.CollectionName)]
public sealed class ChoAccumulatorCommitCosmosTests(CosmosEmulatorFixture cosmos) : IAsyncLifetime
{
    private static readonly Guid Plan = Guid.Parse("6f1c2b0e-3d4a-4b5c-8e9f-0a1b2c3d4e5f");
    private const string Year = "2026";
    private readonly string _tenant = "t-" + Guid.NewGuid().ToString("N")[..10];
    private ChoAccumulatorService _store = null!;
    private string _database = null!;

    public async Task InitializeAsync()
    {
        cosmos.SkipIfUnavailable();
        var client = cosmos.CreateClient(serializerOptions: new CosmosSerializationOptions
        {
            PropertyNamingPolicy = CosmosPropertyNamingPolicy.CamelCase,
        });
        var db = await cosmos.CreateDatabaseAsync(client, "benefit_engine_accum");
        await CosmosEmulatorFixture.CreateContainerAsync(db, "Accumulators");
        _database = db.Id;
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["CosmosDb:DatabaseName"] = _database,
        }).Build();
        _store = new ChoAccumulatorService(
            new AccumulatorRepositoryCosmos(client, config, NullLogger<AccumulatorRepositoryCosmos>.Instance),
            new Tenant(_tenant), NullLogger<ChoAccumulatorService>.Instance);
    }

    public Task DisposeAsync() => Task.CompletedTask;

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

    [SkippableFact]
    public async Task Commit_IsWrittenOnce_ARepeatIsANoOp()
    {
        var commit = Commit("C1", 100m);

        Assert.Equal(AccumulatorCommitOutcome.Committed, await _store.CommitAsync(commit));
        Assert.Equal(AccumulatorCommitOutcome.AlreadyCommitted, await _store.CommitAsync(commit));

        Assert.Equal(100m, await Read(AccumulatorType.IndividualDeductible));
        Assert.Equal(100m, await Read(AccumulatorType.FamilyDeductible));
    }

    [SkippableFact]
    public async Task Commit_ReplacesTheClaimsOwnAndTheReplacedClaimsWrite_AndClamps()
    {
        await _store.CommitAsync(Commit("C1", 200m));
        await _store.CommitAsync(Commit("C1", 150m));          // re-adjudication of C1
        Assert.Equal(150m, await Read(AccumulatorType.IndividualDeductible));

        await _store.CommitAsync(Commit("C2", 300m, replaces: "C1"));
        Assert.Equal(300m, await Read(AccumulatorType.IndividualDeductible));

        await _store.CommitAsync(Commit("C3", 400m));         // only 200 left under the 500 limit
        Assert.Equal(500m, await Read(AccumulatorType.IndividualDeductible));
    }

    [SkippableTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CommitAndTerminalReversal_InEitherOrder_LeaveNothing(bool commitFirst)
    {
        var commit = Commit("C1", 100m);

        if (commitFirst) Assert.Equal(AccumulatorCommitOutcome.Committed, await _store.CommitAsync(commit));
        await _store.ReverseTerminallyAsync("M1", "S1", Plan, Year, "C1");
        if (!commitFirst)
            Assert.Equal(AccumulatorCommitOutcome.RefusedClaimReversed, await _store.CommitAsync(commit));

        Assert.Equal(0m, await Read(AccumulatorType.IndividualDeductible));
        Assert.Equal(0m, await Read(AccumulatorType.FamilyDeductible));
        Assert.Equal(AccumulatorCommitOutcome.RefusedClaimReversed, await _store.CommitAsync(Commit("C1", 100m)));
    }

    private sealed class Tenant(string id) : IBenefitEngineTenantContext
    {
        public string TenantId { get; } = id;
    }
}
