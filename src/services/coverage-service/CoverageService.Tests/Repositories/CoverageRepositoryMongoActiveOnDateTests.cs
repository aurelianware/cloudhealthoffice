using CloudHealthOffice.Testing.Mongo;
using CoverageService.Models;
using CoverageService.Repositories;
using MongoDB.Driver;

namespace CoverageService.Tests.Repositories;

/// <summary>
/// GetActiveCoverageByMemberIdAsync against a real mongod: a coverage is
/// returned for a service date inside its effective/termination span whatever
/// its current status (a terminated coverage was in force for its span),
/// except statuses that are not in force (Pending, Suspended).
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
    public Task TerminatedCoverage_WithoutTerminationDate_NotFound() => RunAsync(
        new[] { Build("term-no-date", CoverageStatus.Terminated, D(2025, 1, 1)) },
        async repo =>
        {
            (await repo.GetActiveCoverageByMemberIdAsync(Tenant, "M1", D(2025, 3, 15)))
                .Should().BeEmpty();
            (await repo.GetActiveCoverageByMemberIdAsync(Tenant, "M1", D(2030, 1, 1)))
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
    public Task CobraFound_PendingNotFound_EvenOnceEffective() => RunAsync(
        new[]
        {
            Build("pend", CoverageStatus.Pending, D(2025, 1, 1)),
            Build("cobra", CoverageStatus.COBRA, D(2025, 1, 1), D(2025, 12, 31))
        },
        async repo =>
        {
            (await repo.GetActiveCoverageByMemberIdAsync(Tenant, "M1", D(2025, 3, 15)))
                .Select(c => c.Id).Should().Equal("cobra");
        });
    [Fact]
    public Task GetStatusTransitionsDue_ReturnsOpenCoverageWhoseTerminationDateIsReached_InAnyTenant() => RunAsync(
        new[]
        {
            Build("due-today", CoverageStatus.Active, D(2025, 1, 1), D(2025, 7, 1)),
            Build("due-past", CoverageStatus.COBRA, D(2025, 1, 1), D(2025, 6, 30)),
            Build("future", CoverageStatus.Active, D(2025, 1, 1), D(2025, 7, 2)),
            Build("open", CoverageStatus.Active, D(2025, 1, 1)),
            Build("done", CoverageStatus.Terminated, D(2025, 1, 1), D(2025, 6, 30)),
            WithTenant(Build("other-tenant", CoverageStatus.Active, D(2025, 1, 1), D(2025, 6, 1)), "t2")
        },
        async repo =>
        {
            (await repo.GetStatusTransitionsDueAsync(D(2025, 7, 1), 100))
                .Select(c => c.Id).Should().BeEquivalentTo("due-today", "due-past", "other-tenant");
            (await repo.GetStatusTransitionsDueAsync(D(2025, 7, 1), 1)).Should().HaveCount(1);
        });

    [Fact]
    public Task SetStatus_OnlyWhileStatusIsStillTheExpectedOne() => RunAsync(
        new[] { Build("c1", CoverageStatus.Active, D(2025, 1, 1), D(2025, 6, 30)) },
        async repo =>
        {
            (await repo.SetStatusAsync(Tenant, "c1", CoverageStatus.COBRA, CoverageStatus.Terminated, "sweep"))
                .Should().BeFalse();
            (await repo.GetByIdAsync(Tenant, "c1"))!.Status.Should().Be(CoverageStatus.Active);

            (await repo.SetStatusAsync(Tenant, "c1", CoverageStatus.Active, CoverageStatus.Terminated, "sweep"))
                .Should().BeTrue();
            var stored = (await repo.GetByIdAsync(Tenant, "c1"))!;
            stored.Status.Should().Be(CoverageStatus.Terminated);
            stored.LastUpdatedBy.Should().Be("sweep");
            stored.TerminationDate.Should().Be(D(2025, 6, 30));

            (await repo.SetStatusAsync("t2", "c1", CoverageStatus.Terminated, CoverageStatus.Active, "sweep"))
                .Should().BeFalse("another tenant's id never matches");
        });

    [Fact]
    public Task SearchActiveOnly_ExcludesActiveCoverageWhoseTerminationDateHasPassed() => RunAsync(
        new[]
        {
            Build("open", CoverageStatus.Active, D(2025, 1, 1)),
            Build("future-term", CoverageStatus.Active, D(2025, 1, 1), DateTime.UtcNow.Date.AddDays(30)),
            Build("term-passed-not-swept", CoverageStatus.Active, D(2025, 1, 1), DateTime.UtcNow.Date.AddDays(-1)),
            Build("term-today", CoverageStatus.Active, D(2025, 1, 1), DateTime.UtcNow.Date),
            Build("terminated", CoverageStatus.Terminated, D(2025, 1, 1), D(2025, 6, 30))
        },
        async repo =>
        {
            var (items, _) = await repo.SearchAsync(Tenant, memberId: "M1", activeOnly: true, pageSize: 100);
            items.Select(c => c.Id).Should().BeEquivalentTo("open", "future-term");

            (await repo.GetCoverageHistoryAsync(Tenant, "M1", includeTerminated: false))
                .Select(c => c.Id).Should().BeEquivalentTo("open", "future-term");
        });

    private static Coverage WithTenant(Coverage c, string tenant)
    {
        c.TenantId = tenant;
        return c;
    }
}
