using CloudHealthOffice.ReferenceData.Domain;
using CloudHealthOffice.ReferenceData.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using ReferenceDataService.Repositories;
using ReferenceDataService.Repositories.Canonical;
using Xunit;

namespace CloudHealthOffice.ReferenceDataService.Tests;

public sealed class CanonicalReferenceDataRepositoryTests
{
    [Fact]
    public async Task Lookup_selects_the_effective_version()
    {
        await using var context = CreateContext();
        var repository = new CanonicalReferenceDataRepository(context);
        await repository.ImportAsync([Code("old", "2025", "old", new DateOnly(2025, 1, 1), new DateOnly(2025, 12, 31))]);
        await repository.ImportAsync([Code("current", "2026", "current", new DateOnly(2026, 1, 1))]);

        var result = await repository.GetAsync("icd-10-cm", "e11.9", new DateOnly(2026, 8, 14));

        result.Should().NotBeNull();
        result!.Id.Should().Be("current");
        result.Coding.Version.Should().Be("2026");
    }

    [Fact]
    public async Task Tenant_records_are_isolated_and_global_records_remain_visible()
    {
        await using var context = CreateContext();
        var repository = new CanonicalReferenceDataRepository(context);
        await repository.ImportAsync([
            Code("global", "2026", "batch", new DateOnly(2026, 1, 1), code: "A1000"),
            Code("tenant-a", "2026", "batch", new DateOnly(2026, 1, 1), code: "A2000") with { TenantId = "tenant-a" }
        ]);

        var tenantA = await repository.SearchAsync(new ReferenceDataQuery
        {
            CodeSystem = "HCPCS", TenantId = "tenant-a", PageSize = 10
        });
        var tenantB = await repository.SearchAsync(new ReferenceDataQuery
        {
            CodeSystem = "HCPCS", TenantId = "tenant-b", PageSize = 10
        });

        tenantA.Items.Select(x => x.Id).Should().BeEquivalentTo("global", "tenant-a");
        tenantB.Items.Select(x => x.Id).Should().ContainSingle().Which.Should().Be("global");
    }

    [Fact]
    public async Task Search_supports_bounded_pagination()
    {
        await using var context = CreateContext();
        var repository = new CanonicalReferenceDataRepository(context);
        await repository.ImportAsync([
            Code("1", "2026", "page", new DateOnly(2026, 1, 1), code: "A1000"),
            Code("2", "2026", "page", new DateOnly(2026, 1, 1), code: "A2000")
        ]);

        var result = await repository.SearchAsync(new ReferenceDataQuery
        {
            CodeSystem = "HCPCS", Search = "A", SearchMode = ReferenceSearchMode.Prefix,
            Page = 2, PageSize = 1
        });

        result.Total.Should().Be(2);
        result.Items.Should().ContainSingle().Which.Id.Should().Be("2");
        var invalid = () => repository.SearchAsync(new ReferenceDataQuery { CodeSystem = "HCPCS", PageSize = 501 });
        await invalid.Should().ThrowAsync<ArgumentOutOfRangeException>().WithParameterName("PageSize");
    }

    [Fact]
    public async Task Repeated_source_version_checksum_is_idempotent()
    {
        await using var context = CreateContext();
        var repository = new CanonicalReferenceDataRepository(context);
        var records = new[] { Code("1", "2026", "same", new DateOnly(2026, 1, 1)) };

        (await repository.ImportAsync(records)).AlreadyImported.Should().BeFalse();
        var repeated = await repository.ImportAsync(records);

        repeated.AlreadyImported.Should().BeTrue();
        repeated.ImportedCount.Should().Be(0);
        (await context.CanonicalReferenceDataImports.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Same_batch_imported_by_another_tenant_is_not_already_imported()
    {
        await using var context = CreateContext();
        var repository = new CanonicalReferenceDataRepository(context);
        var tenantOne = Code("t1", "2026", "shared-batch", new DateOnly(2026, 1, 1)) with { TenantId = "tenant-1" };
        var tenantTwo = tenantOne with { Id = "t2", TenantId = "tenant-2" };

        (await repository.ImportAsync([tenantOne], "user-1")).AlreadyImported.Should().BeFalse();
        var second = await repository.ImportAsync([tenantTwo], "user-2");

        second.AlreadyImported.Should().BeFalse("tenant-2 never imported this batch; tenant-1's import must not skip it");
        second.ImportedCount.Should().Be(1);
        (await repository.GetAsync("icd-10-cm", "e11.9", new DateOnly(2026, 8, 14), tenantId: "tenant-2"))!
            .Id.Should().Be("t2");
        (await repository.ImportAsync([tenantTwo], "user-2")).AlreadyImported.Should().BeTrue("the same tenant re-importing is idempotent");
    }

    [Fact]
    public async Task Global_and_tenant_imports_of_the_same_batch_are_separate_scopes()
    {
        await using var context = CreateContext();
        var repository = new CanonicalReferenceDataRepository(context);
        var global = Code("g", "2026", "scoped", new DateOnly(2026, 1, 1));
        var tenant = global with { Id = "t", TenantId = "tenant-1" };

        (await repository.ImportAsync([tenant], "tenant-user")).AlreadyImported.Should().BeFalse();
        (await repository.ImportAsync([global], "platform-user")).AlreadyImported.Should().BeFalse();
        (await repository.ImportAsync([global], "platform-user")).AlreadyImported.Should().BeTrue();

        var ledger = await context.CanonicalReferenceDataImports.OrderBy(x => x.TenantScope).ToListAsync();
        ledger.Select(x => x.TenantScope).Should().Equal("global", "tenant-1");
    }

    [Fact]
    public async Task Import_ledger_records_the_actor()
    {
        await using var context = CreateContext();
        var repository = new CanonicalReferenceDataRepository(context);

        await repository.ImportAsync([Code("a", "2026", "actor", new DateOnly(2026, 1, 1)) with { TenantId = "tenant-1" }], "user-42");

        var entry = await context.CanonicalReferenceDataImports.SingleAsync();
        entry.ImportedBy.Should().Be("user-42");
        entry.TenantScope.Should().Be("tenant-1");
        entry.ImportKey.Should().StartWith("tenant-1|");
    }

    [Fact]
    public async Task Mixed_global_and_tenant_batch_is_scoped_to_its_tenant()
    {
        await using var context = CreateContext();
        var repository = new CanonicalReferenceDataRepository(context);
        Func<string, ReferenceCode[]> batch = tenant =>
        [
            Code("g-" + tenant, "2026", "mixed", new DateOnly(2026, 1, 1), code: "A1000"),
            Code("t-" + tenant, "2026", "mixed", new DateOnly(2026, 1, 1), code: "A2000") with { TenantId = tenant }
        ];

        (await repository.ImportAsync(batch("tenant-1"))).AlreadyImported.Should().BeFalse();
        (await repository.ImportAsync(batch("tenant-2"))).AlreadyImported.Should().BeFalse();

        (await context.CanonicalReferenceDataImports.Select(x => x.TenantScope).ToListAsync())
            .Should().BeEquivalentTo("global+tenant-1", "global+tenant-2");
    }

    [Fact]
    public async Task Import_normalizes_indexed_coding_fields()
    {
        await using var context = CreateContext();
        var repository = new CanonicalReferenceDataRepository(context);
        var record = Code("normalized", " v1 ", "normalize", new DateOnly(2026, 1, 1)) with
        {
            Category = " diagnosis ",
            Coding = new ChoCoding { CodeSystem = " icd-10-cm ", Code = " e11.9 ", Version = " v1 " }
        };

        await repository.ImportAsync([record]);

        var stored = await context.CanonicalReferenceCodes.SingleAsync();
        stored.CodeSystem.Should().Be("ICD-10-CM");
        stored.Code.Should().Be("E11.9");
        stored.Version.Should().Be("V1");
        stored.Category.Should().Be("DIAGNOSIS");
        (await repository.GetAsync(" icd-10-cm ", "e11.9", new DateOnly(2026, 8, 14), "v1"))
            .Should().NotBeNull();
    }

    [Fact]
    public async Task Concurrent_duplicate_import_is_reported_as_idempotent()
    {
        var databaseName = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<ReferenceDataContext>()
            .UseInMemoryDatabase(databaseName)
            .Options;
        await using var context = new ConcurrentImportContext(options);
        var repository = new CanonicalReferenceDataRepository(context);

        var result = await repository.ImportAsync([
            Code("1", "2026", "concurrent", new DateOnly(2026, 1, 1))
        ]);

        result.AlreadyImported.Should().BeTrue();
        result.ImportedCount.Should().Be(0);
    }

    private static ReferenceDataContext CreateContext() => new(
        new DbContextOptionsBuilder<ReferenceDataContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private sealed class ConcurrentImportContext : ReferenceDataContext
    {
        private readonly DbContextOptions<ReferenceDataContext> _options;
        private bool _simulateRace = true;

        public ConcurrentImportContext(DbContextOptions<ReferenceDataContext> options) : base(options)
        {
            _options = options;
        }

        public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            if (!_simulateRace)
                return await base.SaveChangesAsync(cancellationToken);

            _simulateRace = false;
            var pendingImport = ChangeTracker.Entries<CanonicalReferenceDataImportEntity>().Single().Entity;
            await using var winner = new ReferenceDataContext(_options);
            winner.CanonicalReferenceDataImports.Add(new CanonicalReferenceDataImportEntity
            {
                ImportKey = pendingImport.ImportKey,
                TenantScope = pendingImport.TenantScope,
                SourceId = pendingImport.SourceId,
                SourceVersion = pendingImport.SourceVersion,
                Checksum = pendingImport.Checksum,
                ImportedAt = pendingImport.ImportedAt,
                RecordCount = pendingImport.RecordCount
            });
            await winner.SaveChangesAsync(cancellationToken);
            throw new DbUpdateException("Simulated concurrent unique-key violation.");
        }
    }

    private static ReferenceCode Code(
        string id,
        string version,
        string checksum,
        DateOnly effectiveFrom,
        DateOnly? effectiveTo = null,
        string code = "E11.9") => new()
    {
        Id = id,
        Coding = new ChoCoding
        {
            CodeSystem = code.StartsWith('A') ? "HCPCS" : "ICD-10-CM",
            Code = code,
            Version = version,
            Display = $"Display {code}"
        },
        Description = $"Description {code}",
        EffectiveFrom = effectiveFrom,
        EffectiveTo = effectiveTo,
        SourceId = "test-source",
        SourceVersion = version,
        LicenseClassification = LicenseClassification.Public,
        ExposureClassification = ExposureClassification.PublicReference,
        ImportedAt = DateTimeOffset.UtcNow,
        Checksum = checksum
    };
}
