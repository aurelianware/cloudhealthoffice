using CloudHealthOffice.Testing.Mongo;
using CoverageService.Models;
using CoverageService.Repositories;
using MongoDB.Driver;

namespace CoverageService.Tests.Repositories;

/// <summary>
/// FHIR Coverage search over real MongoDB (EphemeralMongo), ADR 018: tenant-only
/// results, member and id filters, status and line-of-business sets, a
/// half-open last-updated range, a stable order and an exact total.
/// </summary>
[Collection(MongoRunnerFixture.CollectionName)]
public sealed class CoverageFhirSearchRepositoryTests(MongoRunnerFixture mongo) : IAsyncLifetime
{
    private const string Tenant = "t1";
    private IMongoDatabase _db = null!;
    private CoverageFhirSearchRepository _repo = null!;

    public async Task InitializeAsync()
    {
        _db = mongo.CreateDatabase("coverage_fhir_search");
        _repo = new CoverageFhirSearchRepository(_db);
        await _db.GetCollection<Coverage>(CoverageRepositoryMongo.CollectionName).InsertManyAsync(new[]
        {
            Cov("c-m1-old", "M1", CoverageStatus.Terminated, D(2020, 1, 1), LineOfBusiness.Commercial, updatedDay: 1),
            Cov("c-m1-new", "M1", CoverageStatus.Active, D(2023, 1, 1), LineOfBusiness.Commercial, updatedDay: 2),
            Cov("c-m2", "M2", CoverageStatus.COBRA, D(2022, 6, 1), LineOfBusiness.Medicaid, updatedDay: 3),
            Cov("c-m3", "M3", CoverageStatus.Pending, D(2027, 1, 1), LineOfBusiness.Medicare, updatedDay: 4),
            Cov("c-m4", "M4", CoverageStatus.Suspended, D(2021, 1, 1), LineOfBusiness.Medicaid, updatedDay: 5),
            Cov("c-other", "M1", CoverageStatus.Active, D(2023, 1, 1), LineOfBusiness.Commercial, updatedDay: 2, tenant: "t2"),
        });
    }

    public Task DisposeAsync() => mongo.DropDatabaseAsync(_db);

    private static DateTime D(int y, int m, int d) => new(y, m, d, 0, 0, 0, DateTimeKind.Utc);

    private static Coverage Cov(
        string id, string member, CoverageStatus status, DateTime effective, LineOfBusiness lob,
        int updatedDay, string tenant = Tenant) => new()
    {
        Id = id,
        TenantId = tenant,
        MemberId = member,
        GroupNumber = "G1",
        PlanId = "P1",
        Status = status,
        EffectiveDate = effective,
        LineOfBusiness = lob,
        LastUpdatedDate = new DateTime(2026, 4, updatedDay, 9, 0, 0, DateTimeKind.Utc),
    };

    private async Task<(string[] Ids, long Total)> Search(CoverageFhirSearchQuery query, string tenant = Tenant)
    {
        var (items, total) = await _repo.SearchAsync(tenant, query);
        return (items.Select(c => c.Id).ToArray(), total);
    }

    [Fact]
    public async Task Unfiltered_returns_the_tenants_coverage_by_member_newest_first_with_the_total()
    {
        var (ids, total) = await Search(new CoverageFhirSearchQuery { Limit = 100 });

        ids.Should().Equal("c-m1-new", "c-m1-old", "c-m2", "c-m3", "c-m4");
        total.Should().Be(5);
    }

    [Fact]
    public async Task Another_tenants_coverage_is_never_returned_even_by_exact_id()
    {
        (await Search(new CoverageFhirSearchQuery { CoverageId = "c-other" })).Ids.Should().BeEmpty();
        (await Search(new CoverageFhirSearchQuery { MemberId = "M1" }, tenant: "t2")).Ids.Should().Equal("c-other");
    }

    [Fact]
    public async Task Member_and_id_filters_are_exact()
    {
        (await Search(new CoverageFhirSearchQuery { MemberId = "M1" })).Ids.Should().Equal("c-m1-new", "c-m1-old");
        (await Search(new CoverageFhirSearchQuery { MemberId = "M" })).Ids.Should().BeEmpty();
        (await Search(new CoverageFhirSearchQuery { CoverageId = "c-m2" })).Ids.Should().Equal("c-m2");
    }

    [Fact]
    public async Task Status_and_line_of_business_are_sets()
    {
        (await Search(new CoverageFhirSearchQuery
        {
            Statuses = [CoverageStatus.Active, CoverageStatus.COBRA, CoverageStatus.Suspended],
        })).Ids.Should().Equal("c-m1-new", "c-m2", "c-m4");

        (await Search(new CoverageFhirSearchQuery { Statuses = [CoverageStatus.Terminated] }))
            .Ids.Should().Equal("c-m1-old");

        (await Search(new CoverageFhirSearchQuery { LinesOfBusiness = [LineOfBusiness.Medicaid, LineOfBusiness.Medicare] }))
            .Ids.Should().Equal("c-m2", "c-m3", "c-m4");
    }

    [Fact]
    public async Task Last_updated_range_is_inclusive_from_and_exclusive_to()
    {
        var (ids, _) = await Search(new CoverageFhirSearchQuery
        {
            LastUpdatedFrom = new DateTime(2026, 4, 2, 9, 0, 0, DateTimeKind.Utc),
            LastUpdatedTo = new DateTime(2026, 4, 4, 9, 0, 0, DateTimeKind.Utc),
        });

        ids.Should().Equal("c-m1-new", "c-m2");
    }

    [Fact]
    public async Task Pages_are_contiguous_and_limit_zero_returns_only_the_total()
    {
        (await Search(new CoverageFhirSearchQuery { Offset = 0, Limit = 2 })).Ids.Should().Equal("c-m1-new", "c-m1-old");
        (await Search(new CoverageFhirSearchQuery { Offset = 2, Limit = 2 })).Ids.Should().Equal("c-m2", "c-m3");
        (await Search(new CoverageFhirSearchQuery { Offset = 4, Limit = 2 })).Ids.Should().Equal("c-m4");

        var countOnly = await Search(new CoverageFhirSearchQuery { Limit = 0 });
        countOnly.Ids.Should().BeEmpty();
        countOnly.Total.Should().Be(5);
    }
}
