using System.Text;
using BenefitPlanService.HostedServices;
using BenefitPlanService.Models;
using BenefitPlanService.Repositories;
using BenefitPlanService.Tests.Controllers;
using BenefitPlanService.Tests.Fakes;
using CloudHealthOffice.BenefitEngine.Domain;
using CloudHealthOffice.BenefitEngine.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace BenefitPlanService.Tests.Services;

/// <summary>
/// Capability BP 5.6 — verifies the seed bundle parser, the per-tenant
/// idempotency record, and the version-bump delta path (only mappings /
/// rules introduced since the tenant's applied version, never overriding or
/// restoring the tenant's own rows).
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
    public async Task EnsureTenantSeededAsync_Version_Bump_Writes_Only_The_Categories_Introduced_Since()
    {
        // v1 with 2 mappings.
        var (seeder1, store) = BuildAndStart(SimpleBundle(version: 1, count: 2));
        await seeder1.EnsureTenantSeededAsync("tenant-a");
        (await store.ListAsync("tenant-a", null)).Should().HaveCount(2);

        // v2 keeps both and adds Category-2 (since 2); same store.
        var (seeder2, _) = BuildAndStart(SimpleBundle(version: 2, count: 3, newFromIndex: 2), reuseStore: store);
        var written = await seeder2.EnsureTenantSeededAsync("tenant-a");

        written.Should().Be(1, "only the category introduced in v2 is new to the tenant");
        var all = await store.ListAsync("tenant-a", null);
        all.Should().HaveCount(3, "v1 categories are not re-inserted (no duplicates)");
        all.Select(m => m.ServiceTypeCode).Should().OnlyHaveUniqueItems();
        (await store.GetAsync("tenant-a"))!.AppliedSeedVersion.Should().Be(2);
    }

    [Fact]
    public async Task EnsureTenantSeededAsync_Version_Bump_Keeps_Operator_Mappings_Winning()
    {
        // v2: Office Visit 99201-99215 (since 1). Then the operator maps
        // 99213 to their own category and REV 0651 to Home Health.
        var (seeder2, store) = BuildAndStart(OperatorBundle(version: 2));
        await seeder2.EnsureTenantSeededAsync("tenant-a");
        await store.CreateAsync(TenantMapping("Specialist Visit", "CPT", "99213"));
        await store.CreateAsync(TenantMapping("Home Health", "REV", "0651"));

        // v3 adds Hospice REV 0650-0659 (since 3).
        var (seeder3, _) = BuildAndStart(OperatorBundle(version: 3), reuseStore: store);
        await seeder3.EnsureTenantSeededAsync("tenant-a");

        var resolver = new ServiceCategoryResolver(store, NullLogger<ServiceCategoryResolver>.Instance);
        (await Resolve(resolver, "99213", null))!.ServiceTypeCode.Should().Be("Specialist Visit",
            "Office Visit is not re-inserted as a newer row that would outrank the operator");
        (await Resolve(resolver, "", "0651"))!.ServiceTypeCode.Should().Be("Home Health",
            "the new Hospice range overlaps the operator's REV 0651 rule, so it is not added");
        (await Resolve(resolver, "99214", null))!.ServiceTypeCode.Should().Be("Office Visit");
        (await store.ListAsync("tenant-a", null)).Should().NotContain(m => m.ServiceTypeCode == "Hospice");
    }

    [Fact]
    public async Task EnsureTenantSeededAsync_Version_Bump_Adds_A_New_Category_That_Fills_A_Gap()
    {
        var (seeder2, store) = BuildAndStart(OperatorBundle(version: 2));
        await seeder2.EnsureTenantSeededAsync("tenant-a");

        var (seeder3, _) = BuildAndStart(OperatorBundle(version: 3), reuseStore: store);
        var written = await seeder3.EnsureTenantSeededAsync("tenant-a");

        written.Should().Be(1);
        var resolver = new ServiceCategoryResolver(store, NullLogger<ServiceCategoryResolver>.Instance);
        (await Resolve(resolver, "", "0651"))!.ServiceTypeCode.Should().Be("Hospice");
    }

    [Fact]
    public async Task EnsureTenantSeededAsync_Version_Bump_Does_Not_Restore_A_Deleted_Default()
    {
        var (seeder2, store) = BuildAndStart(OperatorBundle(version: 2));
        await seeder2.EnsureTenantSeededAsync("tenant-a");
        var outpatient = (await store.ListAsync("tenant-a", null)).Single(m => m.ServiceTypeCode == "Outpatient Hospital");
        (await store.DeleteAsync("tenant-a", outpatient.Id)).Should().BeTrue();

        var (seeder3, _) = BuildAndStart(OperatorBundle(version: 3), reuseStore: store);
        await seeder3.EnsureTenantSeededAsync("tenant-a");

        (await store.ListAsync("tenant-a", null)).Should().NotContain(m => m.ServiceTypeCode == "Outpatient Hospital",
            "Outpatient Hospital was introduced in v2, which the tenant already applied");
    }

    [Fact]
    public async Task EnsureTenantSeededAsync_Version_Bump_Is_Idempotent()
    {
        var (seeder2, store) = BuildAndStart(OperatorBundle(version: 2));
        await seeder2.EnsureTenantSeededAsync("tenant-a");

        var (seeder3, _) = BuildAndStart(OperatorBundle(version: 3), reuseStore: store);
        var first = await seeder3.EnsureTenantSeededAsync("tenant-a");
        var countAfterFirst = (await store.ListAsync("tenant-a", null)).Count;
        var second = await seeder3.EnsureTenantSeededAsync("tenant-a");

        first.Should().Be(1);
        second.Should().Be(0);
        (await store.ListAsync("tenant-a", null)).Should().HaveCount(countAfterFirst);
    }

    [Fact]
    public async Task EnsureTenantSeededAsync_Version_Bump_Adds_A_Rule_Introduced_Into_An_Existing_Category()
    {
        const string v1 = """
            {"version": 1, "mappings": [
              {"serviceTypeCode": "Imaging", "serviceTypeDescription": "Imaging",
               "rules": [{"priority": 10, "codeType": "CPT", "codePattern": "70000", "codeRangeEnd": "79999"}]}]}
            """;
        const string v2 = """
            {"version": 2, "mappings": [
              {"serviceTypeCode": "Imaging", "serviceTypeDescription": "Imaging",
               "rules": [{"priority": 10, "codeType": "CPT", "codePattern": "70000", "codeRangeEnd": "79999"},
                         {"priority": 20, "codeType": "REV", "codePattern": "0320", "codeRangeEnd": "0359", "since": 2}]}]}
            """;
        var (seeder1, store) = BuildAndStart(v1);
        await seeder1.EnsureTenantSeededAsync("tenant-a");

        var (seeder2, _) = BuildAndStart(v2, reuseStore: store);
        (await seeder2.EnsureTenantSeededAsync("tenant-a")).Should().Be(1);

        var added = (await store.ListAsync("tenant-a", null))
            .Where(m => m.Rules.Any(r => r.CodeType == "REV")).Should().ContainSingle().Subject;
        added.Rules.Should().ContainSingle("only the v2 rule is written, not the v1 CPT range again");
    }

    [Fact]
    public void StartAsync_Rejects_A_Since_Later_Than_The_Bundle_Version()
    {
        const string bad = """
            {"version": 2, "mappings": [
              {"serviceTypeCode": "X", "serviceTypeDescription": "X", "since": 3,
               "rules": [{"priority": 1, "codeType": "CPT", "codePattern": "99213"}]}]}
            """;
        var (seeder, _) = BuildAndStart(bad);

        seeder.LoadedBundle.Should().BeNull();
    }

    [Fact]
    public async Task Shipped_Bundle_Upgrade_From_V2_Writes_Only_Hospice()
    {
        var (seeder, store) = BuildAndStart(File.ReadAllText(ShippedBundlePath()));
        seeder.LoadedBundle!.Version.Should().Be(3);

        // A tenant that applied v2: every pre-v3 row in place, record at v2.
        await seeder.EnsureTenantSeededAsync("tenant-a");
        var hospice = (await store.ListAsync("tenant-a", null)).Single(m => m.ServiceTypeCode == "Hospice");
        await store.DeleteAsync("tenant-a", hospice.Id);
        await store.UpsertAsync(new SystemDefaultsAppliedRecord { TenantId = "tenant-a", AppliedSeedVersion = 2 });
        var before = (await store.ListAsync("tenant-a", null)).Count;

        var written = await seeder.EnsureTenantSeededAsync("tenant-a");

        written.Should().Be(1);
        var all = await store.ListAsync("tenant-a", null);
        all.Should().HaveCount(before + 1);
        all.Should().ContainSingle(m => m.ServiceTypeCode == "Hospice");
    }

    [Fact]
    public void Shipped_Bundle_Hospice_Relies_On_Revenue_Codes_Only()
    {
        // HCPCS Q5001-Q5009 read "hospice OR home health care provided in
        // <setting>", so a line carrying them is not necessarily hospice;
        // REV 065x is hospice-only.
        var (seeder, _) = BuildAndStart(File.ReadAllText(ShippedBundlePath()));
        var hospice = seeder.LoadedBundle!.Mappings.Single(m => m.ServiceTypeCode == "Hospice");

        hospice.Since.Should().Be(3);
        hospice.Rules.Should().OnlyContain(r => r.CodeType == "REV");
        seeder.LoadedBundle.Mappings.Single(m => m.ServiceTypeCode == "Outpatient Hospital").Since.Should().Be(2);
    }

    [Theory]
    [InlineData("CPT", "99201", "99215", "CPT", "99213", null, true)]
    [InlineData("CPT", "99201", "99215", "CPT", "99216", null, false)]
    [InlineData("CPT", "99201", "99215", "HCPCS", "99213", null, false)]
    [InlineData("HCPCS", "J0000", "J9999", "HCPCS", "J*", null, true)]
    [InlineData("HCPCS", "K0001", "K0999", "HCPCS", "J*", null, false)]
    [InlineData("REV", "0650", "0659", "REV", "651", null, true)]
    [InlineData("REV", "0650", "0659", "REV", "0640", "0649", false)]
    public void Overlaps_ComparesCodeTypeAndSpans(
        string seedType, string seedFrom, string? seedTo,
        string existingType, string existingFrom, string? existingTo, bool expected)
    {
        var seed = new SystemDefaultMappingSeeder.SeedRule { CodeType = seedType, CodePattern = seedFrom, CodeRangeEnd = seedTo };
        var existing = new ProcedureCodeRule { CodeType = existingType, CodePattern = existingFrom, CodeRangeEnd = existingTo };

        SystemDefaultMappingSeeder.Overlaps(seed, existing).Should().Be(expected);
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

    /// <summary>
    /// <paramref name="count"/> mappings, each with one distinct CPT code;
    /// mappings at index &gt;= <paramref name="newFromIndex"/> declare
    /// <c>since</c> = <paramref name="version"/>.
    /// </summary>
    private static string SimpleBundle(int version, int count, int? newFromIndex = null)
    {
        string Since(int i) => newFromIndex is not null && i >= newFromIndex ? $"\"since\": {version}," : "";

        var mappings = string.Join(",", Enumerable.Range(0, count).Select(i => $$"""
            {
              "serviceTypeCode": "Category-{{i}}",
              "serviceTypeDescription": "Category {{i}}",
              {{Since(i)}}
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

    /// <summary>
    /// Office Visit (since 1), Outpatient Hospital (since 2) and, from v3,
    /// Hospice REV 0650-0659 (since 3) — the shape of the shipped bundle.
    /// </summary>
    private static string OperatorBundle(int version)
    {
        var hospice = version >= 3
            ? """
              ,{"serviceTypeCode": "Hospice", "serviceTypeDescription": "Hospice", "since": 3,
                "rules": [{"priority": 10, "codeType": "REV", "codePattern": "0650", "codeRangeEnd": "0659"}]}
              """
            : "";
        return $$"""
        {
          "version": {{version}},
          "mappings": [
            {"serviceTypeCode": "Office Visit", "serviceTypeDescription": "Office Visit",
             "rules": [{"priority": 10, "codeType": "CPT", "codePattern": "99201", "codeRangeEnd": "99215"}]},
            {"serviceTypeCode": "Outpatient Hospital", "serviceTypeDescription": "Outpatient Hospital", "since": 2,
             "rules": [{"priority": 10, "codeType": "HCPCS", "codePattern": "G0463", "codeRangeEnd": "G0463"}]}
            {{hospice}}
          ]
        }
        """;
    }

    private static ServiceCategoryMapping TenantMapping(string category, string codeType, string code) => new()
    {
        TenantId = "tenant-a",
        BenefitPlanId = null,
        ServiceTypeCode = category,
        ServiceTypeDescription = category,
        Rules = [new ProcedureCodeRule { Id = Guid.NewGuid(), Priority = 1, CodeType = codeType, CodePattern = code }],
        IsActive = true,
        // CreatedAt left unset: the store stamps "now", after the v2 seed
        // rows and before anything the v3 seed writes (which, re-inserted,
        // would sort first and win).
    };

    private static Task<ServiceCategoryMatch?> Resolve(
        ServiceCategoryResolver resolver, string procedureCode, string? revenueCode)
        => resolver.ResolveAsync("tenant-a", Guid.NewGuid(), new DateOnly(2026, 3, 8),
            procedureCode, "CPT", "11", [], revenueCode);

    private static string ShippedBundlePath()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "schemas", "service-category-mappings", "system-defaults.json");
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("schemas/service-category-mappings/system-defaults.json not found above the test output.");
    }
}
