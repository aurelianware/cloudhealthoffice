using CHO.TerminologyService.Data;
using CHO.TerminologyService.Models;
using EphemeralMongo;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;

namespace CloudHealthOffice.TerminologyService.Tests;

public sealed class MongoConceptMapRepositoryTests : IAsyncLifetime
{
    private const string Icd10CmSystem = "http://hl7.org/fhir/sid/icd-10-cm";
    private const string SnomedSystem = "http://snomed.info/sct";

    private IMongoRunner _runner = null!;
    private IMongoDatabase _database = null!;
    private MongoConceptMapRepository _repository = null!;

    public Task InitializeAsync()
    {
        _runner = MongoRunner.Run(new MongoRunnerOptions { ConnectionTimeout = TimeSpan.FromSeconds(30) });
        var client = new MongoClient(_runner.ConnectionString);
        _database = client.GetDatabase($"terminology_repo_test_{Guid.NewGuid():N}");
        _repository = new MongoConceptMapRepository(
            _database,
            NullLogger<MongoConceptMapRepository>.Instance);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        try { _runner.Dispose(); }
        catch (TypeLoadException) { /* EphemeralMongo.Core 2.0.0 / MongoDB.Driver 3.x disposal mismatch. */ }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task FindDisplaysByCodeAsync_ReturnsActiveEntriesAndTenantOverrideFirst()
    {
        await _database.GetCollection<MapVersion>("map_versions").InsertManyAsync(
        [
            new()
            {
                Id = "active-map",
                MapName = "SNOMED-ICD10",
                SourceSystem = SnomedSystem,
                TargetSystem = Icd10CmSystem,
                IsActive = true
            },
            new()
            {
                Id = "old-map",
                MapName = "SNOMED-ICD10",
                SourceSystem = SnomedSystem,
                TargetSystem = Icd10CmSystem,
                IsActive = false
            }
        ]);
        await _database.GetCollection<ConceptMapEntry>("concept_map_entries").InsertManyAsync(
        [
            Entry("active-priority-2", mapVersionId: "active-map", priority: 2, targetDisplay: "Active target display"),
            Entry("active-priority-1", mapVersionId: "active-map", priority: 1, targetDisplay: "Active preferred display"),
            Entry("inactive", mapVersionId: "old-map", priority: 0, targetDisplay: "Inactive display"),
            Entry("tenant-a-override", isOverride: true, tenantId: "tenant-a", priority: 99, targetDisplay: "Tenant A display"),
            Entry("tenant-b-override", isOverride: true, tenantId: "tenant-b", priority: 0, targetDisplay: "Tenant B display")
        ]);

        var results = await _repository.FindDisplaysByCodeAsync(Icd10CmSystem, "E11.65", "tenant-a");

        Assert.Collection(
            results,
            entry => Assert.Equal("tenant-a-override", entry.Id),
            entry => Assert.Equal("active-priority-1", entry.Id),
            entry => Assert.Equal("active-priority-2", entry.Id));
    }

    [Fact]
    public async Task FindDisplaysByCodeAsync_WithoutTenant_DoesNotReturnOverrides()
    {
        await _database.GetCollection<MapVersion>("map_versions").InsertOneAsync(new MapVersion
        {
            Id = "active-map",
            MapName = "SNOMED-ICD10",
            SourceSystem = SnomedSystem,
            TargetSystem = Icd10CmSystem,
            IsActive = true
        });
        await _database.GetCollection<ConceptMapEntry>("concept_map_entries").InsertManyAsync(
        [
            Entry("active", mapVersionId: "active-map", targetDisplay: "Active target display"),
            Entry("tenant-a-override", isOverride: true, tenantId: "tenant-a", targetDisplay: "Tenant A display")
        ]);

        var results = await _repository.FindDisplaysByCodeAsync(Icd10CmSystem, "E11.65");

        var result = Assert.Single(results);
        Assert.Equal("active", result.Id);
    }

    private static ConceptMapEntry Entry(
        string id,
        string mapVersionId = "override",
        bool isOverride = false,
        string? tenantId = null,
        int priority = 1,
        string targetDisplay = "Type 2 diabetes mellitus with hyperglycemia")
    {
        return new ConceptMapEntry
        {
            Id = id,
            SourceSystem = SnomedSystem,
            SourceCode = "44054006",
            SourceDisplay = "Diabetes mellitus type 2",
            TargetSystem = Icd10CmSystem,
            TargetCode = "E11.65",
            TargetDisplay = targetDisplay,
            MapVersionId = mapVersionId,
            IsOverride = isOverride,
            TenantId = tenantId,
            Priority = priority
        };
    }
}
