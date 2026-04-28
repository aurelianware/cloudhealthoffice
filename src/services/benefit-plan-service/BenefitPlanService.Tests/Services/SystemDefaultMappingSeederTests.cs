using System.Text;
using BenefitPlanService.HostedServices;
using BenefitPlanService.Models;
using BenefitPlanService.Tests.Controllers;
using BenefitPlanService.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace BenefitPlanService.Tests.Services;

/// <summary>
/// Capability BP 5.6 — verifies the seed bundle parser, the per-tenant
/// idempotency record, and the version-bump re-apply path.
/// </summary>
public sealed class SystemDefaultMappingSeederTests : IDisposable
{
    private readonly string _tempDir;

    public SystemDefaultMappingSeederTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"sc-seeder-{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true);
    }

    [Fact]
    public async Task EnsureTenantSeededAsync_Writes_Mappings_On_First_Run()
    {
        var (seeder, store) = BuildAndStart(SimpleBundle(version: 1, count: 3));

        var written = await seeder.EnsureTenantSeededAsync("tenant-a");

        written.Should().Be(3);
        var mappings = await store.ListAsync("tenant-a", null);
        mappings.Should().HaveCount(3);
        mappings.Should().AllSatisfy(m =>
        {
            m.TenantId.Should().Be("tenant-a");
            m.BenefitPlanId.Should().BeNull("seed mappings are tenant-default scope");
            m.IsActive.Should().BeTrue();
        });

        var applied = await store.GetAsync("tenant-a");
        applied.Should().NotBeNull();
        applied!.AppliedSeedVersion.Should().Be(1);
        applied.MappingCount.Should().Be(3);
    }

    [Fact]
    public async Task EnsureTenantSeededAsync_Is_Idempotent_At_Same_Version()
    {
        var (seeder, store) = BuildAndStart(SimpleBundle(version: 2, count: 2));

        var first = await seeder.EnsureTenantSeededAsync("tenant-a");
        var second = await seeder.EnsureTenantSeededAsync("tenant-a");

        first.Should().Be(2);
        second.Should().Be(0, "rerun at the same bundle version is a no-op");
        (await store.ListAsync("tenant-a", null)).Should().HaveCount(2);
    }

    [Fact]
    public async Task EnsureTenantSeededAsync_Re_Applies_When_Bundle_Version_Increases()
    {
        // Initial bundle version 1 with 2 mappings.
        var (seeder1, store) = BuildAndStart(SimpleBundle(version: 1, count: 2));
        await seeder1.EnsureTenantSeededAsync("tenant-a");
        (await store.ListAsync("tenant-a", null)).Should().HaveCount(2);

        // Bump bundle to version 2 with 3 mappings; same store.
        var (seeder2, _) = BuildAndStart(SimpleBundle(version: 2, count: 3), reuseStore: store);
        var written = await seeder2.EnsureTenantSeededAsync("tenant-a");

        written.Should().Be(3, "version bump re-applies; total grows by the new count");
        var allMappings = await store.ListAsync("tenant-a", null);
        allMappings.Should().HaveCount(5, "v1 rows are preserved alongside v2 rows; operators clean up via DELETE");
        var applied = await store.GetAsync("tenant-a");
        applied!.AppliedSeedVersion.Should().Be(2);
    }

    [Fact]
    public async Task EnsureTenantSeededAsync_Returns_Zero_When_Bundle_Failed_To_Load()
    {
        var (seeder, _) = BuildAndStart(bundleJson: null); // no file

        var written = await seeder.EnsureTenantSeededAsync("tenant-a");

        written.Should().Be(0);
        seeder.LoadedBundle.Should().BeNull();
    }

    [Fact]
    public async Task StartAsync_Validates_Bundle_Schema_And_Marks_Bundle_Null_On_Error()
    {
        // Missing version field → validation fail → bundle null.
        var bad = """{"mappings":[{"serviceTypeCode":"X","serviceTypeDescription":"X","rules":[{"priority":1,"codeType":"CPT","codePattern":"99213"}]}]}""";
        var (seeder, _) = BuildAndStart(bad);

        seeder.LoadedBundle.Should().BeNull("zero/missing version must fail validation");

        var written = await seeder.EnsureTenantSeededAsync("tenant-a");
        written.Should().Be(0);
    }

    [Fact]
    public async Task LoadedBundle_Includes_All_Defined_Mappings()
    {
        var (seeder, _) = BuildAndStart(SimpleBundle(version: 1, count: 5));

        seeder.LoadedBundle.Should().NotBeNull();
        seeder.LoadedBundle!.Mappings.Should().HaveCount(5);
        seeder.LoadedBundle.Version.Should().Be(1);

        await Task.CompletedTask;
    }

    private (SystemDefaultMappingSeeder seeder, InMemoryServiceCategoryMappingStore store)
        BuildAndStart(string? bundleJson, InMemoryServiceCategoryMappingStore? reuseStore = null)
    {
        var path = Path.Combine(_tempDir, $"bundle-{Guid.NewGuid()}.json");
        if (bundleJson is not null)
        {
            File.WriteAllText(path, bundleJson, Encoding.UTF8);
        }

        var options = new ServiceCategoryMappingOptions
        {
            SeedFilePath = path,
            SeedSystemDefaultsOnStartup = true,
        };
        var monitor = new TestOptionsMonitor<ServiceCategoryMappingOptions>(options);
        var hostEnv = new TestHostEnvironment();
        var store = reuseStore ?? new InMemoryServiceCategoryMappingStore();
        var sp = new TestServiceProvider(store, store);
        var seeder = new SystemDefaultMappingSeeder(
            sp, monitor, hostEnv, NullLogger<SystemDefaultMappingSeeder>.Instance);
        seeder.StartAsync(default).GetAwaiter().GetResult();
        return (seeder, store);
    }

    private static string SimpleBundle(int version, int count)
    {
        var mappings = string.Join(",", Enumerable.Range(0, count).Select(i => $$"""
            {
              "serviceTypeCode": "Category-{{i}}",
              "serviceTypeDescription": "Category {{i}}",
              "rules": [{"priority": 10, "codeType": "CPT", "codePattern": "9921{{i % 10}}"}]
            }
            """));
        return $$"""
        {
          "version": {{version}},
          "source": "Test bundle",
          "mappings": [{{mappings}}]
        }
        """;
    }
}
