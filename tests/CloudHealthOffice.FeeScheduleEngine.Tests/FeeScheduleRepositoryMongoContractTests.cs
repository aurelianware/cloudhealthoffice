using CloudHealthOffice.FeeScheduleEngine.Domain;
using CloudHealthOffice.FeeScheduleEngine.Models;
using CloudHealthOffice.FeeScheduleEngine.Persistence;
using CloudHealthOffice.Testing.Mongo;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;

namespace CloudHealthOffice.FeeScheduleEngine.Tests;

/// <summary>
/// <see cref="FeeScheduleRepositoryMongo"/> contract lookup and schedule
/// round trip against a real mongod, ported from the Cosmos emulator suite:
/// an open-ended contract (no <c>TermDate</c>) is in force, by NPI and by
/// group TIN; a <c>TermDate</c> bounds the contract; a schedule reads back
/// with its type and rates; another tenant sees none of it.
/// </summary>
[Collection(MongoRunnerFixture.CollectionName)]
public sealed class FeeScheduleRepositoryMongoContractTests(MongoRunnerFixture mongo) : IAsyncLifetime
{
    private const string Tenant = "tenant-fs-contracts";
    private IMongoDatabase _db = null!;
    private FeeScheduleRepositoryMongo _repo = null!;

    public Task InitializeAsync()
    {
        _db = mongo.CreateDatabase("fs_contracts");
        _repo = new FeeScheduleRepositoryMongo(_db, new ConfigurationBuilder().Build(), NullLogger<FeeScheduleRepositoryMongo>.Instance);
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => mongo.DropDatabaseAsync(_db);

    private static ProviderContract Contract(string npi, string planId, DateTime? termDate) => new()
    {
        Id = ProviderContract.MakeId(Tenant, npi, planId),
        TenantId = Tenant,
        ProviderNpi = npi,
        GroupTin = "12-3456789",
        PlanId = planId,
        EffectiveDate = new DateTime(2026, 1, 1),
        TermDate = termDate,
        NetworkStatus = NetworkStatus.InNetwork,
        FeeScheduleId = "fs-" + planId,
    };

    private Task<ProviderContract?> Find(string npi, string? groupTin, string planId, DateTime serviceDate) =>
        _repo.GetContractInternalAsync(Tenant, npi, groupTin, planId, serviceDate, CancellationToken.None);

    [Fact]
    public async Task OpenEndedContract_IsInForce()
    {
        await _repo.UpsertAsync(Contract("1111111111", "plan-a", termDate: null));

        var found = await Find("1111111111", null, "plan-a", new DateTime(2026, 6, 1));

        Assert.NotNull(found);
        Assert.Null(found!.TermDate);
        Assert.Equal(NetworkStatus.InNetwork, found.NetworkStatus);
        Assert.Null(await _repo.GetContractInternalAsync(
            "other-tenant", "1111111111", null, "plan-a", new DateTime(2026, 6, 1), CancellationToken.None));
    }

    [Fact]
    public async Task OpenEndedContract_IsFoundByGroupTin()
    {
        await _repo.UpsertAsync(Contract("2222222222", "plan-a", termDate: null));

        var found = await Find("9999999999", "12-3456789", "plan-a", new DateTime(2026, 6, 1));

        Assert.Equal("2222222222", found!.ProviderNpi);
    }

    [Fact]
    public async Task TermDate_BoundsTheContract()
    {
        await _repo.UpsertAsync(Contract("3333333333", "plan-a", termDate: new DateTime(2026, 3, 31)));

        Assert.NotNull(await Find("3333333333", null, "plan-a", new DateTime(2026, 2, 1)));
        Assert.Null(await Find("3333333333", null, "plan-a", new DateTime(2026, 6, 1)));
    }

    [Fact]
    public async Task Schedule_UpsertAndList_RoundTrip()
    {
        var schedule = new FeeSchedule
        {
            Id = FeeSchedule.MakeId(Tenant, "Commercial 2026", new DateTime(2026, 1, 1)),
            TenantId = Tenant,
            Name = "Commercial 2026",
            Type = FeeScheduleType.Custom,
            EffectiveDate = new DateTime(2026, 1, 1),
            Lines = [new FeeScheduleLine { ProcedureCode = "99213", RateType = FeeScheduleRateType.FlatRate, Rate = 95.5m }],
        };

        await _repo.UpsertAsync(schedule);

        var read = await _repo.GetByIdAsync(Tenant, schedule.Id);
        Assert.Equal(FeeScheduleType.Custom, read!.Type);
        Assert.Equal(95.5m, read.Lines.Single().Rate);
        Assert.Single(await _repo.ListAsync(Tenant));
        Assert.Empty(await _repo.ListAsync("other-tenant"));
    }
}
