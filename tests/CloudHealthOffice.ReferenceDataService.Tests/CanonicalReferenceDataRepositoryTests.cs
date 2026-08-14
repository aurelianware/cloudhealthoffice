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

    private static ReferenceDataContext CreateContext() => new(
        new DbContextOptionsBuilder<ReferenceDataContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

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
