using CloudHealthOffice.ProviderEnrollmentService.Cache;
using CloudHealthOffice.ProviderEnrollmentService.Models;
using CloudHealthOffice.Testing.Mongo;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;

namespace CloudHealthOffice.ProviderEnrollmentService.Tests;

/// <summary>
/// <see cref="EnrollmentRepositoryMongo"/> on a real mongod: the behaviours
/// <see cref="EnrollmentRepositoryCosmosTests"/> pins on a mocked Cosmos
/// container (read-through hit and miss, one record per NPI and state,
/// state-scoped revalidation and MCO panel queries, idempotent delete),
/// ported ahead of the Cosmos repository's removal.
/// </summary>
[Collection(MongoRunnerFixture.CollectionName)]
public sealed class EnrollmentRepositoryMongoTests(MongoRunnerFixture mongo) : IAsyncLifetime
{
    private const string Npi = "1234567890";
    private IMongoDatabase _db = null!;
    private EnrollmentRepositoryMongo _sut = null!;

    public Task InitializeAsync()
    {
        _db = mongo.CreateDatabase("enrollment_cache");
        _sut = new EnrollmentRepositoryMongo(_db,
            Options.Create(new ProviderEnrollmentOptions { CacheTtl = TimeSpan.FromHours(4) }),
            NullLogger<EnrollmentRepositoryMongo>.Instance);
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => mongo.DropDatabaseAsync(_db);

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);

    private static StateEnrollmentRecord Record(
        string npi = Npi, string state = "TX", EnrollmentStatus status = EnrollmentStatus.Active,
        string mco = "MCO-001", int? revalidationInDays = null) => new()
    {
        Npi = npi,
        StateCode = state,
        SourceSystem = "PEMS",
        Status = status,
        EffectiveDate = new DateOnly(2023, 1, 15),
        ProviderType = ProviderTypeClassification.PhysicianMD,
        SupportedLobs = LineOfBusiness.Medicaid | LineOfBusiness.STAR,
        McoParticipation = [mco],
        RevalidationDueDate = revalidationInDays is { } days ? Today.AddDays(days) : null,
    };

    [Fact]
    public async Task Get_ReturnsTheStoredRecord_MarkedAsFromCache()
    {
        await _sut.UpsertAsync(Record());

        var read = await _sut.GetAsync(Npi, "TX");

        read.Should().NotBeNull();
        read!.Npi.Should().Be(Npi);
        read.StateCode.Should().Be("TX");
        read.Status.Should().Be(EnrollmentStatus.Active);
        read.ProviderType.Should().Be(ProviderTypeClassification.PhysicianMD);
        read.SupportedLobs.Should().Be(LineOfBusiness.Medicaid | LineOfBusiness.STAR);
        read.McoParticipation.Should().Equal("MCO-001");
        read.IsFromCache.Should().BeTrue();
    }

    [Fact]
    public async Task Get_Missing_ReturnsNull()
    {
        (await _sut.GetAsync(Npi, "TX")).Should().BeNull();
    }

    [Fact]
    public async Task Upsert_KeepsOneRecordPerNpiAndState()
    {
        await _sut.UpsertAsync(Record());
        await _sut.UpsertAsync(Record(status: EnrollmentStatus.Terminated));
        await _sut.UpsertAsync(Record(state: "CA"));

        (await _sut.GetAsync(Npi, "TX"))!.Status.Should().Be(EnrollmentStatus.Terminated, "the second upsert replaced the first");
        (await _sut.GetAllStatesAsync(Npi)).Select(r => r.StateCode).Should().BeEquivalentTo(new[] { "TX", "CA" });
        (await _db.GetCollection<BsonDocument>("enrollment_cache").CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty))
            .Should().Be(2);
    }

    [Fact]
    public async Task RevalidationDueSoon_IsActiveWithinTheWindow_AndScopedToTheState()
    {
        await _sut.UpsertAsync(Record(npi: "1000000001", revalidationInDays: 10));
        await _sut.UpsertAsync(Record(npi: "1000000002", revalidationInDays: 200));
        await _sut.UpsertAsync(Record(npi: "1000000003", revalidationInDays: 10, status: EnrollmentStatus.Terminated));
        await _sut.UpsertAsync(Record(npi: "1000000004", state: "CA", revalidationInDays: 10));

        (await _sut.GetProvidersWithRevalidationDueSoonAsync(90, "TX")).Select(r => r.Npi)
            .Should().Equal("1000000001");
        (await _sut.GetProvidersWithRevalidationDueSoonAsync(90)).Select(r => r.Npi)
            .Should().BeEquivalentTo(new[] { "1000000001", "1000000004" });
    }

    [Fact]
    public async Task ActivePanelByMco_IsTheStatesActiveParticipants_OfThatMco()
    {
        await _sut.UpsertAsync(Record(npi: "1000000001"));
        await _sut.UpsertAsync(Record(npi: "1000000002", mco: "MCO-002"));
        await _sut.UpsertAsync(Record(npi: "1000000003", status: EnrollmentStatus.Terminated));
        await _sut.UpsertAsync(Record(npi: "1000000004", state: "CA"));

        var panel = await _sut.GetActivePanelByMcoAsync("TX", "MCO-001");

        panel.Should().ContainSingle();
        panel[0].Npi.Should().Be("1000000001");
        panel[0].StateCode.Should().Be("TX");
    }

    [Fact]
    public async Task Delete_RemovesTheRecord_AndAMissingRecordIsANoOp()
    {
        await _sut.UpsertAsync(Record());

        await _sut.DeleteAsync(Npi, "TX");
        (await _sut.GetAsync(Npi, "TX")).Should().BeNull();

        var again = () => _sut.DeleteAsync(Npi, "TX");
        await again.Should().NotThrowAsync();
    }
}
