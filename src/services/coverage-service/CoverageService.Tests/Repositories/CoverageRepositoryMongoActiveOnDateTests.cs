using CloudHealthOffice.Testing.Mongo;
using CoverageService.Models;
using CoverageService.Repositories;
using MongoDB.Driver;

namespace CoverageService.Tests.Repositories;

/// <summary>
/// GetActiveCoverageByMemberIdAsync against a real mongod: a coverage is
/// returned for a service date inside its effective/termination span whatever
/// its current status (a terminated coverage was in force for its span),
/// except statuses that are not in force (Suspended).
/// </summary>
[Collection(MongoRunnerFixture.CollectionName)]
public class CoverageRepositoryMongoActiveOnDateTests
{
    private const string Tenant = "t1";
    private readonly MongoRunnerFixture _mongo;

    public CoverageRepositoryMongoActiveOnDateTests(MongoRunnerFixture mongo) => _mongo = mongo;

    private static DateTime D(int y, int m, int d) => new(y, m, d, 0, 0, 0, DateTimeKind.Utc);

    private static Coverage Build(string id, CoverageStatus status, DateTime effective, DateTime? termination = null) => new()
    {
        Id = id,
        TenantId = Tenant,
        MemberId = "M1",
        GroupNumber = "G",
        PlanId = "P",
        Status = status,
        EffectiveDate = effective,
        TerminationDate = termination
    };

    private async Task RunAsync(Coverage[] seed, Func<CoverageRepositoryMongo, Task> assert)
    {
        var db = _mongo.CreateDatabase("coverage_dos");
        try
        {
            var repo = new CoverageRepositoryMongo(db);
            foreach (var c in seed) await repo.CreateAsync(c);
            await assert(repo);
        }
        finally
        {
            await _mongo.DropDatabaseAsync(db);
        }
    }

    [Fact]
    public Task TerminatedCoverage_FoundForServiceDateOnOrBeforeTermDate() => RunAsync(
        new[] { Build("term", CoverageStatus.Terminated, D(2025, 1, 1), D(2025, 6, 30)) },
        async repo =>
        {
            (await repo.GetActiveCoverageByMemberIdAsync(Tenant, "M1", D(2025, 3, 15)))
                .Select(c => c.Id).Should().Equal("term");
            (await repo.GetActiveCoverageByMemberIdAsync(Tenant, "M1", D(2025, 6, 30)))
                .Select(c => c.Id).Should().Equal("term");
        });

    [Fact]
    public Task TerminatedCoverage_NotFoundForServiceDateAfterTermDate() => RunAsync(
        new[] { Build("term", CoverageStatus.Terminated, D(2025, 1, 1), D(2025, 6, 30)) },
        async repo =>
        {
            (await repo.GetActiveCoverageByMemberIdAsync(Tenant, "M1", D(2025, 7, 1)))
                .Should().BeEmpty();
        });

    [Fact]
    public Task SuspendedCoverage_ExcludedEvenWithinSpan() => RunAsync(
        new[] { Build("susp", CoverageStatus.Suspended, D(2025, 1, 1)) },
        async repo =>
        {
            (await repo.GetActiveCoverageByMemberIdAsync(Tenant, "M1", D(2025, 3, 15)))
                .Should().BeEmpty();
        });

    [Fact]
    public Task ActiveOpenEndedCoverage_Found_FutureCoverageNot() => RunAsync(
        new[]
        {
            Build("open", CoverageStatus.Active, D(2025, 1, 1)),
            Build("future", CoverageStatus.Pending, D(2026, 1, 1))
        },
        async repo =>
        {
            (await repo.GetActiveCoverageByMemberIdAsync(Tenant, "M1", D(2025, 3, 15)))
                .Select(c => c.Id).Should().Equal("open");
        });

    [Fact]
    public Task PendingAndCobraCoverage_FoundOnceEffective() => RunAsync(
        new[]
        {
            Build("pend", CoverageStatus.Pending, D(2025, 1, 1)),
            Build("cobra", CoverageStatus.COBRA, D(2025, 1, 1), D(2025, 12, 31))
        },
        async repo =>
        {
            (await repo.GetActiveCoverageByMemberIdAsync(Tenant, "M1", D(2025, 3, 15)))
                .Select(c => c.Id).Should().BeEquivalentTo(new[] { "pend", "cobra" });
        });
}
