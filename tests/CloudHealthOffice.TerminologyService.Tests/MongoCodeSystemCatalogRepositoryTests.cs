using CHO.TerminologyService.Data;
using CHO.TerminologyService.Models;
using CloudHealthOffice.Testing.Mongo;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;

namespace CloudHealthOffice.TerminologyService.Tests;

[Collection(MongoRunnerFixture.CollectionName)]
public sealed class MongoCodeSystemCatalogRepositoryTests : IAsyncLifetime
{
    private const string Icd10CmSystem = "http://hl7.org/fhir/sid/icd-10-cm";

    private readonly MongoRunnerFixture _mongo;
    private IMongoDatabase _database = null!;
    private MongoCodeSystemCatalogRepository _repository = null!;

    public MongoCodeSystemCatalogRepositoryTests(MongoRunnerFixture mongo) => _mongo = mongo;

    public Task InitializeAsync()
    {
        _database = _mongo.CreateDatabase("code_system_catalog_test");
        _repository = new MongoCodeSystemCatalogRepository(
            _database,
            NullLogger<MongoCodeSystemCatalogRepository>.Instance);
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => _mongo.DropDatabaseAsync(_database);

    [Fact]
    public async Task FindDisplayAsync_ReturnsGlobalDisplay()
    {
        await _repository.UpsertManyAsync(
        [
            Concept("E11.65", "Type 2 diabetes mellitus with hyperglycemia")
        ]);

        var display = await _repository.FindDisplayAsync(Icd10CmSystem, "e11.65");

        Assert.NotNull(display);
        Assert.Equal("Type 2 diabetes mellitus with hyperglycemia", display.Display);
        Assert.Equal("BuiltInIcd10CmCatalog", display.Source);
        Assert.Equal("mcc-seed-2026", display.Version);
    }

    [Fact]
    public async Task FindDisplayAsync_TenantOverrideWinsOverGlobalDisplay()
    {
        await _repository.UpsertManyAsync(
        [
            Concept("K08.1", "Complete loss of teeth"),
            Concept(
                "K08.1",
                "Plan-specific complete tooth loss display",
                tenantId: "tenant-a",
                isOverride: true,
                source: "PlanCatalogOverride")
        ]);

        var display = await _repository.FindDisplayAsync(Icd10CmSystem, "K08.1", "tenant-a");

        Assert.NotNull(display);
        Assert.Equal("Plan-specific complete tooth loss display", display.Display);
        Assert.Equal("CodeSystemOverride", display.Source);
    }

    [Fact]
    public async Task FindDisplayAsync_WithoutTenant_DoesNotReturnTenantOverride()
    {
        await _repository.UpsertManyAsync(
        [
            Concept("K08.1", "Complete loss of teeth"),
            Concept(
                "K08.1",
                "Plan-specific complete tooth loss display",
                tenantId: "tenant-a",
                isOverride: true)
        ]);

        var display = await _repository.FindDisplayAsync(Icd10CmSystem, "K08.1");

        Assert.NotNull(display);
        Assert.Equal("Complete loss of teeth", display.Display);
        Assert.Equal("BuiltInIcd10CmCatalog", display.Source);
    }

    private static CodeSystemConcept Concept(
        string code,
        string display,
        string? tenantId = null,
        bool isOverride = false,
        string source = "BuiltInIcd10CmCatalog")
    {
        return new CodeSystemConcept
        {
            System = Icd10CmSystem,
            Code = code,
            Display = display,
            Version = "mcc-seed-2026",
            Source = source,
            TenantId = tenantId,
            IsOverride = isOverride
        };
    }
}
