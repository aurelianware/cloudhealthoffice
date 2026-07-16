using CHO.TerminologyService.Data;
using CHO.TerminologyService.Models;
using EphemeralMongo;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;

namespace CloudHealthOffice.TerminologyService.Tests;

public sealed class MongoCodeSystemCatalogRepositoryTests : IAsyncLifetime
{
    private const string Icd10CmSystem = "http://hl7.org/fhir/sid/icd-10-cm";

    private IMongoRunner _runner = null!;
    private MongoCodeSystemCatalogRepository _repository = null!;

    public Task InitializeAsync()
    {
        _runner = MongoRunner.Run(new MongoRunnerOptions { ConnectionTimeout = TimeSpan.FromSeconds(30) });
        var client = new MongoClient(_runner.ConnectionString);
        var database = client.GetDatabase($"code_system_catalog_test_{Guid.NewGuid():N}");
        _repository = new MongoCodeSystemCatalogRepository(
            database,
            NullLogger<MongoCodeSystemCatalogRepository>.Instance);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        try { _runner.Dispose(); }
        catch (TypeLoadException) { /* EphemeralMongo.Core 2.0.0 / MongoDB.Driver 3.x disposal mismatch. */ }
        return Task.CompletedTask;
    }

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
