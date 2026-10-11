using CloudHealthOffice.FeeScheduleEngine.Domain;
using CloudHealthOffice.FeeScheduleEngine.Models;
using CloudHealthOffice.FeeScheduleEngine.Persistence;
using CloudHealthOffice.Infrastructure.Serialization;
using CloudHealthOffice.Testing.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudHealthOffice.FeeScheduleEngine.Tests;

/// <summary>
/// <see cref="FeeScheduleRepositoryCosmos"/> on the Cosmos DB emulator with the
/// serializer production uses (<see cref="CosmosSystemTextJsonSerializer"/>,
/// which omits null properties): an open-ended contract has no stored
/// <c>termDate</c> at all, and must still be found (D-31).
/// </summary>
[Trait("Category", CosmosEmulator.Category)]
[Collection(CosmosEmulatorFixture.CollectionName)]
public sealed class FeeScheduleRepositoryCosmosTests(CosmosEmulatorFixture cosmos) : IAsyncLifetime
{
    private readonly string _tenant = "t-" + Guid.NewGuid().ToString("N")[..10];
    private FeeScheduleRepositoryCosmos _repo = null!;

    public async Task InitializeAsync()
    {
        cosmos.SkipIfUnavailable();
        var client = cosmos.CreateClient(new CosmosSystemTextJsonSerializer());
        var db = await cosmos.CreateDatabaseAsync(client, "fee_schedule");
        await CosmosEmulatorFixture.CreateContainerAsync(db, "FeeSchedules");
        await CosmosEmulatorFixture.CreateContainerAsync(db, "ProviderContracts");
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["CosmosDb:DatabaseName"] = db.Id,
        }).Build();
        _repo = new FeeScheduleRepositoryCosmos(client, config, NullLogger<FeeScheduleRepositoryCosmos>.Instance);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private ProviderContract Contract(string npi, string planId, DateTime? termDate) => new()
    {
        Id = ProviderContract.MakeId(_tenant, npi, planId),
        TenantId = _tenant,
        ProviderNpi = npi,
        GroupTin = "12-3456789",
        PlanId = planId,
        EffectiveDate = new DateTime(2026, 1, 1),
        TermDate = termDate,
        NetworkStatus = NetworkStatus.InNetwork,
        FeeScheduleId = "fs-" + planId,
    };

    [SkippableFact]
    public async Task OpenEndedContract_IsInForce()
    {
        await _repo.UpsertAsync(Contract("1111111111", "plan-a", termDate: null));

        var found = await _repo.GetContractAsync(_tenant, "1111111111", null, "plan-a", new DateTime(2026, 6, 1));

        Assert.NotNull(found);
        Assert.Null(found!.TermDate);
        Assert.Equal(NetworkStatus.InNetwork, found.NetworkStatus);
    }

    [SkippableFact]
    public async Task OpenEndedContract_IsFoundByGroupTin()
    {
        await _repo.UpsertAsync(Contract("2222222222", "plan-a", termDate: null));

        var found = await _repo.GetContractAsync(_tenant, "9999999999", "12-3456789", "plan-a", new DateTime(2026, 6, 1));

        Assert.Equal("2222222222", found!.ProviderNpi);
    }

    [SkippableFact]
    public async Task TermDate_BoundsTheContract()
    {
        await _repo.UpsertAsync(Contract("3333333333", "plan-a", termDate: new DateTime(2026, 3, 31)));

        Assert.NotNull(await _repo.GetContractAsync(_tenant, "3333333333", null, "plan-a", new DateTime(2026, 2, 1)));
        Assert.Null(await _repo.GetContractAsync(_tenant, "3333333333", null, "plan-a", new DateTime(2026, 6, 1)));
    }

    [SkippableFact]
    public async Task Schedule_UpsertAndList_RoundTrip()
    {
        var schedule = new FeeSchedule
        {
            Id = FeeSchedule.MakeId(_tenant, "Commercial 2026", new DateTime(2026, 1, 1)),
            TenantId = _tenant,
            Name = "Commercial 2026",
            Type = FeeScheduleType.Custom,
            EffectiveDate = new DateTime(2026, 1, 1),
            Lines = [new FeeScheduleLine { ProcedureCode = "99213", RateType = FeeScheduleRateType.FlatRate, Rate = 95.5m }],
        };

        await _repo.UpsertAsync(schedule);

        var read = await _repo.GetByIdAsync(_tenant, schedule.Id);
        Assert.Equal(FeeScheduleType.Custom, read!.Type);
        Assert.Equal(95.5m, read.Lines.Single().Rate);
        Assert.Single(await _repo.ListAsync(_tenant));
    }
}
