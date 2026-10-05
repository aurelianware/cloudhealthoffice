using System.Text;
using CHO.TerminologyService.Data;
using CHO.TerminologyService.Migrations;
using CHO.TerminologyService.Models;
using CHO.TerminologyService.Services;
using CHO.TerminologyService.Services.Loaders;
using CloudHealthOffice.Testing.Mongo;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;

namespace CloudHealthOffice.TerminologyService.Tests;

/// <summary>
/// Override loads of the same file by two tenants do not collide, and the
/// backfill assigns legacy override map versions to their tenant.
/// </summary>
[Collection(MongoRunnerFixture.CollectionName)]
public sealed class OverrideTenancyMigrationTests : IAsyncLifetime
{
    private const string Snomed = "http://snomed.info/sct";
    private const string Icd10 = "http://hl7.org/fhir/sid/icd-10-cm";

    private readonly MongoRunnerFixture _mongo;
    private IMongoDatabase _database = null!;
    private MongoConceptMapRepository _repository = null!;

    public OverrideTenancyMigrationTests(MongoRunnerFixture mongo) => _mongo = mongo;

    public Task InitializeAsync()
    {
        _database = _mongo.CreateDatabase("terminology_override_tenancy");
        _repository = new MongoConceptMapRepository(_database, NullLogger<MongoConceptMapRepository>.Instance);
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => _mongo.DropDatabaseAsync(_database);

    private IMongoCollection<MapVersion> Versions => _database.GetCollection<MapVersion>("map_versions");
    private IMongoCollection<ConceptMapEntry> Entries => _database.GetCollection<ConceptMapEntry>("concept_map_entries");

    private static MemoryStream Csv() => new(Encoding.UTF8.GetBytes(
        "source_code,target_code,target_display\n44054006,E11.9,Type 2 diabetes\n38341003,I10,Hypertension\n"));

    private static MapLoadOptions Override(string tenant) => new()
    {
        MapName = "PLAN-OVERRIDES",
        Version = "2026-01",
        SourceSystem = Snomed,
        TargetSystem = Icd10,
        TenantId = tenant,
        IsOverride = true,
    };

    [Fact]
    public async Task TwoTenants_LoadingTheSameOverrideFile_AtOnce_EachGetTheirOwnVersionAndEntries()
    {
        var loader = new CsvMapLoader(_repository, NullLogger<CsvMapLoader>.Instance);

        var a = await loader.LoadAsync(Csv(), Override("tenant-a"));
        var b = await loader.LoadAsync(Csv(), Override("tenant-b"));

        a.Success.Should().BeTrue(string.Join("; ", a.Errors));
        b.Success.Should().BeTrue(string.Join("; ", b.Errors));
        a.MapVersionId.Should().NotBe(b.MapVersionId);
        a.MapVersionId.Should().Contain("tenant-a");

        (await Versions.Find(v => v.Id == a.MapVersionId).SingleAsync()).TenantId.Should().Be("tenant-a");
        (await Versions.Find(v => v.Id == b.MapVersionId).SingleAsync()).TenantId.Should().Be("tenant-b");
        (await Entries.CountDocumentsAsync(e => e.TenantId == "tenant-a" && e.IsOverride)).Should().Be(2);
        (await Entries.CountDocumentsAsync(e => e.TenantId == "tenant-b" && e.IsOverride)).Should().Be(2);
        var ids = await Entries.Find(FilterDefinition<ConceptMapEntry>.Empty).Project(e => e.Id).ToListAsync();
        ids.Should().OnlyHaveUniqueItems();
        ids.Where(id => id.Contains("tenant-a")).Should().HaveCount(2);
    }

    [Fact]
    public void OverrideVersionIds_CannotBeForgedByATenantAndMapNameCombination()
    {
        var one = MapVersionIds.For(new MapLoadOptions { MapName = "b:m", Version = "1", TenantId = "a", IsOverride = true });
        var two = MapVersionIds.For(new MapLoadOptions { MapName = "m", Version = "1", TenantId = "a:b", IsOverride = true });

        one.Should().StartWith("override:1:a:");
        two.Should().StartWith("override:3:a:b:");
    }

    [Fact]
    public async Task Backfill_AssignsOverrideVersionsToTheirTenant_AndLeavesTheRest()
    {
        await LegacyVersion("global-map");
        await Entries.InsertOneAsync(Entry("g1", "global-map", isOverride: false, tenant: null));

        await LegacyVersion("acme-overrides");
        await Entries.InsertManyAsync(new[]
        {
            Entry("o1", "acme-overrides", isOverride: true, tenant: "acme"),
            Entry("o2", "acme-overrides", isOverride: true, tenant: "acme"),
        });

        await LegacyVersion("mixed-tenants");
        await Entries.InsertManyAsync(new[]
        {
            Entry("m1", "mixed-tenants", isOverride: true, tenant: "acme"),
            Entry("m2", "mixed-tenants", isOverride: true, tenant: "beta"),
        });

        await LegacyVersion("empty-version");

        await Versions.InsertOneAsync(new MapVersion { Id = "already-scoped", MapName = "X", TenantId = "beta" });
        await Entries.InsertOneAsync(Entry("s1", "already-scoped", isOverride: true, tenant: "beta"));

        var dry = await BackfillOverrideVersionTenants.BackfillAsync(_database, dryRun: true);
        dry.Assigned.Should().Be(1);
        (await Versions.Find(v => v.Id == "acme-overrides").SingleAsync()).TenantId.Should().BeNull("a dry run changes nothing");

        var counts = await BackfillOverrideVersionTenants.BackfillAsync(_database, dryRun: false);

        counts.Scanned.Should().Be(4);
        counts.Assigned.Should().Be(1);
        counts.Global.Should().Be(1);
        counts.Ambiguous.Should().Equal("mixed-tenants");
        counts.NoEntries.Should().Equal("empty-version");
        counts.Complete.Should().BeFalse();

        (await Versions.Find(v => v.Id == "acme-overrides").SingleAsync()).TenantId.Should().Be("acme");
        (await Versions.Find(v => v.Id == "global-map").SingleAsync()).TenantId.Should().BeNull();
        (await Versions.Find(v => v.Id == "mixed-tenants").SingleAsync()).TenantId.Should().BeNull();
        (await Versions.Find(v => v.Id == "already-scoped").SingleAsync()).TenantId.Should().Be("beta");

        // Now invisible to other tenants.
        (await _repository.GetMapVersionsVisibleToAsync("beta")).Select(v => v.Id).Should().NotContain("acme-overrides");

        // Idempotent.
        var again = await BackfillOverrideVersionTenants.BackfillAsync(_database, dryRun: false);
        again.Assigned.Should().Be(0);
        again.Scanned.Should().Be(3);
    }

    /// <summary>A version saved before MapVersion had a TenantId: the field is absent.</summary>
    private async Task LegacyVersion(string id)
    {
        var doc = new MapVersion { Id = id, MapName = id, SourceSystem = Snomed, TargetSystem = Icd10, IsActive = true }.ToBsonDocument();
        doc.Remove("TenantId");
        await _database.GetCollection<BsonDocument>("map_versions").InsertOneAsync(doc);
    }

    private static ConceptMapEntry Entry(string id, string version, bool isOverride, string? tenant) => new()
    {
        Id = id,
        SourceSystem = Snomed,
        SourceCode = "44054006",
        TargetSystem = Icd10,
        TargetCode = "E11.9",
        MapVersionId = version,
        IsOverride = isOverride,
        TenantId = tenant,
    };
}
