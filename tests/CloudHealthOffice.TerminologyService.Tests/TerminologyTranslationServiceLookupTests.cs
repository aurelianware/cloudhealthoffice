using CHO.TerminologyService.Configuration;
using CHO.TerminologyService.Models;
using CHO.TerminologyService.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace CloudHealthOffice.TerminologyService.Tests;

public sealed class TerminologyTranslationServiceLookupTests
{
    private const string Icd10CmSystem = "http://hl7.org/fhir/sid/icd-10-cm";
    private const string SnomedSystem = "http://snomed.info/sct";

    [Fact]
    public async Task LookupCodeAsync_CodeSystemCatalogHit_ReturnsCatalogDisplayWithoutConceptMapLookup()
    {
        var repository = Substitute.For<IConceptMapRepository>();
        var catalog = Substitute.For<ICodeSystemCatalogRepository>();
        catalog.FindDisplayAsync(Icd10CmSystem, "E11.65", null, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<CodeSystemDisplay?>(
                new CodeSystemDisplay(
                    "Type 2 diabetes mellitus with hyperglycemia",
                    "mcc-seed-2026",
                    "BuiltInIcd10CmCatalog")));
        var service = CreateService(repository, catalog);

        var response = await service.LookupCodeAsync(new CodeLookupRequest
        {
            System = Icd10CmSystem,
            Code = "E11.65"
        });

        Assert.True(response.Result);
        Assert.Equal("Type 2 diabetes mellitus with hyperglycemia", response.Display);
        Assert.Equal("BuiltInIcd10CmCatalog", response.Source);
        Assert.Equal("mcc-seed-2026", response.MapVersionId);
        await repository.DidNotReceiveWithAnyArgs()
            .FindDisplaysByCodeAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task LookupCodeAsync_SourceCodeMatch_ReturnsSourceDisplay()
    {
        var repository = Substitute.For<IConceptMapRepository>();
        repository.FindDisplaysByCodeAsync(Icd10CmSystem, "E11.65", null, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<ConceptMapEntry>
            {
                new()
                {
                    SourceSystem = Icd10CmSystem,
                    SourceCode = "E11.65",
                    SourceDisplay = "Type 2 diabetes mellitus with hyperglycemia",
                    TargetSystem = SnomedSystem,
                    TargetCode = "44054006",
                    TargetDisplay = "Diabetes mellitus type 2",
                    MapVersionId = "ICD10-SNOMED-2026"
                }
            }));
        var service = CreateService(repository);

        var response = await service.LookupCodeAsync(new CodeLookupRequest
        {
            System = Icd10CmSystem,
            Code = "E11.65"
        });

        Assert.True(response.Result);
        Assert.Equal("Type 2 diabetes mellitus with hyperglycemia", response.Display);
        Assert.Equal("ConceptMapSource", response.Source);
        Assert.Equal("ICD10-SNOMED-2026", response.MapVersionId);
    }

    [Fact]
    public async Task LookupCodeAsync_TargetCodeMatch_ReturnsTargetDisplay()
    {
        var repository = Substitute.For<IConceptMapRepository>();
        repository.FindDisplaysByCodeAsync(Icd10CmSystem, "E11.65", null, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<ConceptMapEntry>
            {
                new()
                {
                    SourceSystem = SnomedSystem,
                    SourceCode = "44054006",
                    SourceDisplay = "Diabetes mellitus type 2",
                    TargetSystem = Icd10CmSystem,
                    TargetCode = "E11.65",
                    TargetDisplay = "Type 2 diabetes mellitus with hyperglycemia",
                    MapVersionId = "SNOMED-ICD10-2026"
                }
            }));
        var service = CreateService(repository);

        var response = await service.LookupCodeAsync(new CodeLookupRequest
        {
            System = Icd10CmSystem,
            Code = "E11.65"
        });

        Assert.True(response.Result);
        Assert.Equal("Type 2 diabetes mellitus with hyperglycemia", response.Display);
        Assert.Equal("ConceptMapTarget", response.Source);
        Assert.Equal("SNOMED-ICD10-2026", response.MapVersionId);
    }

    [Fact]
    public async Task LookupCodeAsync_BlankCandidateDisplay_ReturnsFirstPopulatedDisplay()
    {
        var repository = Substitute.For<IConceptMapRepository>();
        repository.FindDisplaysByCodeAsync(Icd10CmSystem, "E11.65", null, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<ConceptMapEntry>
            {
                new()
                {
                    SourceSystem = SnomedSystem,
                    SourceCode = "44054006",
                    SourceDisplay = "Diabetes mellitus type 2",
                    TargetSystem = Icd10CmSystem,
                    TargetCode = "E11.65",
                    TargetDisplay = "",
                    MapVersionId = "SNOMED-ICD10-RF2"
                },
                new()
                {
                    SourceSystem = SnomedSystem,
                    SourceCode = "73211009",
                    SourceDisplay = "Diabetes mellitus",
                    TargetSystem = Icd10CmSystem,
                    TargetCode = "E11.65",
                    TargetDisplay = "Type 2 diabetes mellitus with hyperglycemia",
                    MapVersionId = "SNOMED-ICD10-CURATED"
                }
            }));
        var service = CreateService(repository);

        var response = await service.LookupCodeAsync(new CodeLookupRequest
        {
            System = Icd10CmSystem,
            Code = "E11.65"
        });

        Assert.True(response.Result);
        Assert.Equal("Type 2 diabetes mellitus with hyperglycemia", response.Display);
        Assert.Equal("SNOMED-ICD10-CURATED", response.MapVersionId);
    }

    [Fact]
    public async Task LookupCodeAsync_TenantOverride_ReturnsPlanOverrideSource()
    {
        var repository = Substitute.For<IConceptMapRepository>();
        repository.FindDisplaysByCodeAsync(Icd10CmSystem, "K08.1", "tenant-a", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<ConceptMapEntry>
            {
                new()
                {
                    SourceSystem = Icd10CmSystem,
                    SourceCode = "K08.1",
                    SourceDisplay = "Loss of teeth due to accident, extraction, or local periodontal disease",
                    TargetSystem = SnomedSystem,
                    TargetCode = "25540007",
                    TargetDisplay = "Loss of teeth",
                    MapVersionId = "PLAN-OVERRIDE-1",
                    IsOverride = true,
                    TenantId = "tenant-a"
                }
            }));
        var service = CreateService(repository);

        var response = await service.LookupCodeAsync(new CodeLookupRequest
        {
            System = Icd10CmSystem,
            Code = "K08.1",
            TenantId = "tenant-a"
        });

        Assert.True(response.Result);
        Assert.Equal("Loss of teeth due to accident, extraction, or local periodontal disease", response.Display);
        Assert.Equal("PlanOverride", response.Source);
        await repository.Received(1)
            .FindDisplaysByCodeAsync(Icd10CmSystem, "K08.1", "tenant-a", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LookupCodeAsync_NoPopulatedDisplay_ReturnsFalse()
    {
        var repository = Substitute.For<IConceptMapRepository>();
        repository.FindDisplaysByCodeAsync(Icd10CmSystem, "E11.65", null, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<ConceptMapEntry>
            {
                new()
                {
                    SourceSystem = SnomedSystem,
                    SourceCode = "44054006",
                    SourceDisplay = "Diabetes mellitus type 2",
                    TargetSystem = Icd10CmSystem,
                    TargetCode = "E11.65",
                    TargetDisplay = "",
                    MapVersionId = "SNOMED-ICD10-RF2"
                }
            }));
        var service = CreateService(repository);

        var response = await service.LookupCodeAsync(new CodeLookupRequest
        {
            System = Icd10CmSystem,
            Code = "E11.65"
        });

        Assert.False(response.Result);
        Assert.Null(response.Display);
        Assert.Contains("No display found", response.Message);
    }

    private static TerminologyTranslationService CreateService(
        IConceptMapRepository repository,
        ICodeSystemCatalogRepository? catalog = null)
    {
        if (catalog is null)
        {
            catalog = Substitute.For<ICodeSystemCatalogRepository>();
            catalog.FindDisplayAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<CodeSystemDisplay?>(null));
        }

        return new TerminologyTranslationService(
            repository,
            catalog,
            Substitute.For<IContextRuleEngine>(),
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<TerminologyTranslationService>.Instance,
            Options.Create(new TerminologyServiceOptions()));
    }
}
