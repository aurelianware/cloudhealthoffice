using System.Text;
using BenefitPlanService.HostedServices;
using BenefitPlanService.Models;
using BenefitPlanService.Repositories;
using BenefitPlanService.Tests.Controllers;
using BenefitPlanService.Tests.Fakes;
using CloudHealthOffice.BenefitEngine.Domain;
using Microsoft.Extensions.Logging.Abstractions;

namespace BenefitPlanService.Tests.Repositories;

public sealed class SystemDefaultFallbackMappingRepositoryTests : IDisposable
{
    private const string Bundle = """
        {
          "version": 4,
          "mappings": [
            {"serviceTypeCode": "5", "serviceTypeDescription": "Diagnostic Lab",
             "rules": [{"priority": 10, "codeType": "CPT", "codePattern": "80047", "codeRangeEnd": "89398"}]},
            {"serviceTypeCode": "98", "serviceTypeDescription": "Office Visit",
             "rules": [{"priority": 20, "codeType": "CPT", "codePattern": "99202", "codeRangeEnd": "99215"}]}
          ]
        }
        """;

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"sc-fallback-{Guid.NewGuid()}.json");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    [Fact]
    public async Task Tenant_without_mappings_reads_the_system_defaults()
    {
        var (repo, _) = Build();

        var mappings = await repo.GetMappingsAsync("tenant-a", null);

        mappings.Select(m => m.ServiceTypeCode).Should().Equal("5", "98");
        mappings.Should().AllSatisfy(m =>
        {
            m.TenantId.Should().Be("tenant-a");
            m.BenefitPlanId.Should().BeNull();
            m.IsActive.Should().BeTrue();
        });
        mappings[0].Rules.Single().CodePattern.Should().Be("80047");
    }

    [Fact]
    public async Task Defaults_have_stable_ids_and_are_never_written()
    {
        var (repo, store) = Build();

        var first = await repo.GetMappingsAsync("tenant-a", null);
        var second = await repo.GetMappingsAsync("tenant-a", null);

        second.Select(m => m.Id).Should().Equal(first.Select(m => m.Id));
        (await store.ListAsync("tenant-a", null)).Should().BeEmpty();
        (await store.GetAsync("tenant-a")).Should().BeNull("the fallback does not mark the tenant seeded");
    }

    [Fact]
    public async Task Tenant_with_its_own_mappings_uses_only_those()
    {
        var (repo, store) = Build();
        await store.CreateAsync(new ServiceCategoryMapping
        {
            TenantId = "tenant-a", ServiceTypeCode = "30", ServiceTypeDescription = "Plan coverage",
            IsActive = true, Rules = [new ProcedureCodeRule { Priority = 1, CodeType = "CPT", CodePattern = "*" }],
        });

        var mappings = await repo.GetMappingsAsync("tenant-a", null);

        mappings.Select(m => m.ServiceTypeCode).Should().Equal("30");
    }

    [Fact]
    public async Task Plan_scoped_reads_pass_through()
    {
        var (repo, _) = Build();

        (await repo.GetMappingsAsync("tenant-a", Guid.NewGuid())).Should().BeEmpty();
    }

    [Fact]
    public async Task Fallback_can_be_turned_off()
    {
        var (repo, _) = Build(fallBack: false);

        (await repo.GetMappingsAsync("tenant-a", null)).Should().BeEmpty();
    }

    private (SystemDefaultFallbackMappingRepository repo, InMemoryServiceCategoryMappingStore store) Build(bool fallBack = true)
    {
        File.WriteAllText(_path, Bundle, Encoding.UTF8);
        var options = new ServiceCategoryMappingOptions
        {
            SeedFilePath = _path,
            SeedSystemDefaultsOnStartup = true,
            FallBackToSystemDefaults = fallBack,
        };
        var monitor = new TestOptionsMonitor<ServiceCategoryMappingOptions>(options);
        var store = new InMemoryServiceCategoryMappingStore();
        var seeder = new SystemDefaultMappingSeeder(
            new TestServiceProvider(store, store), monitor, new TestHostEnvironment(),
            NullLogger<SystemDefaultMappingSeeder>.Instance);
        seeder.StartAsync(default).GetAwaiter().GetResult();
        return (new SystemDefaultFallbackMappingRepository(store, seeder, monitor), store);
    }
}
