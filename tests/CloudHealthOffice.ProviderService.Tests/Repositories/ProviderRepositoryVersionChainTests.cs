using EphemeralMongo;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;
using ProviderService.Models;
using ProviderService.Repositories;

namespace CloudHealthOffice.ProviderService.Tests.Repositories;

/// <summary>
/// Mongo-backed coverage for the version chain: hydration of legacy rows,
/// rejection of writes against non-Draft rows, newest-first listing,
/// and the activate / supersede atomic transition.
/// </summary>
public class ProviderRepositoryVersionChainTests : IAsyncLifetime
{
    private const string Tenant = "tenant-a";

    private IMongoRunner _runner = null!;
    private IMongoDatabase _database = null!;
    private ProviderRepositoryMongo _repo = null!;
    private DefaultHttpContext _ctx = null!;

    public Task InitializeAsync()
    {
        _runner = MongoRunner.Run(new MongoRunnerOptions { ConnectionTimeout = TimeSpan.FromSeconds(30) });
        var client = new MongoClient(_runner.ConnectionString);
        _database = client.GetDatabase($"provider_chain_test_{Guid.NewGuid():N}");
        _ctx = new DefaultHttpContext();
        _ctx.Items["TenantId"] = Tenant;
        var accessor = new HttpContextAccessor { HttpContext = _ctx };
        _repo = new ProviderRepositoryMongo(_database, accessor, NullLogger<ProviderRepositoryMongo>.Instance);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        try { _runner.Dispose(); }
        catch (TypeLoadException) { /* see MpipRateServiceTests note */ }
        return Task.CompletedTask;
    }

    private static Provider Sample(string id) => new()
    {
        Id = id,
        ProviderId = id,
        TenantId = Tenant,
        NPI = "1234567890",
        ProviderType = ProviderType.Individual,
        FirstName = "Jane",
        LastName = "Doe",
        PrimarySpecialty = "Internal Medicine",
        TaxonomyCode = "207R00000X"
    };

    [Fact]
    public async Task LegacyRow_hydrates_to_active_v1()
    {
        // Insert a row that predates the version-chain feature: no
        // VersionId / VersionState / VersionNumber set.
        var collection = _database.GetCollection<Provider>("Providers");
        await collection.InsertOneAsync(new Provider
        {
            Id = "legacy-1",
            TenantId = Tenant,
            NPI = "9999999990",
            ProviderType = ProviderType.Individual,
            FirstName = "Legacy",
            LastName = "Row",
            PrimarySpecialty = "Cardiology",
            TaxonomyCode = "207RC0000X",
            Status = ProviderStatus.Active
        });

        var hydrated = await _repo.GetByIdAsync("legacy-1");
        hydrated.Should().NotBeNull();
        hydrated!.VersionState.Should().Be(ProviderVersionState.Active);
        hydrated.VersionId.Should().Be("legacy-1");
        hydrated.VersionNumber.Should().Be(1);
        hydrated.ProviderId.Should().Be("legacy-1");
    }

    [Fact]
    public async Task UpdateAsync_against_active_throws()
    {
        var draft = Sample("p1");
        draft.VersionState = ProviderVersionState.Draft;
        await _repo.CreateDraftAsync(draft);

        var active = await _repo.GetVersionAsync("p1", draft.VersionId);
        active!.VersionState = ProviderVersionState.Active;
        await _repo.ActivateAndSupersedeAsync(active, predecessor: null);

        // Mutating an Active row must surface 409 (state exception).
        active.PrimarySpecialty = "Tampered";
        var act = () => _repo.UpdateAsync(active);
        await act.Should().ThrowAsync<ProviderVersionStateException>()
            .Where(ex => ex.CurrentState == ProviderVersionState.Active);
    }

    [Fact]
    public async Task ListVersionsAsync_paginates_newest_first()
    {
        // Insert two rows on the same chain directly so we can control
        // VersionState (CreateDraftAsync would force them both to Draft).
        var collection = _database.GetCollection<Provider>("Providers");

        var v1 = Sample("p2");
        v1.Id = Guid.NewGuid().ToString();
        v1.ProviderId = "p2";
        v1.VersionId = "VER1";
        v1.VersionNumber = 1;
        v1.VersionState = ProviderVersionState.Superseded;
        await collection.InsertOneAsync(v1);

        var v2 = Sample("p2");
        v2.Id = Guid.NewGuid().ToString();
        v2.ProviderId = "p2";
        v2.VersionId = "VER2";
        v2.VersionNumber = 2;
        v2.VersionState = ProviderVersionState.Active;
        await collection.InsertOneAsync(v2);

        var (items, _) = await _repo.ListVersionsAsync("p2", 25, null);
        items.Should().HaveCount(2);
        items[0].VersionId.Should().Be("VER2");
        items[1].VersionId.Should().Be("VER1");
    }
}
