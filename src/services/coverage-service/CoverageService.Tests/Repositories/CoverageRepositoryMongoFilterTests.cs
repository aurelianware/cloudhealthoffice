using CloudHealthOffice.Testing.Mongo;
using CoverageService.Models;
using CoverageService.Repositories;
using MongoDB.Driver;

namespace CoverageService.Tests.Repositories;

/// <summary>
/// Query filters and the sweep's status write on <see cref="CoverageRepositoryMongo"/>
/// against a real mongod: the insurance-line filter, tenant and member scoping,
/// an unfiltered search, and a status write that leaves a concurrent edit of
/// other fields in place. Ported from the Cosmos query-shape tests when the
/// Cosmos coverage repository was removed (Phase 5b), for the behaviours
/// <see cref="CoverageRepositoryMongoActiveOnDateTests"/> did not already cover.
/// </summary>
[Collection(MongoRunnerFixture.CollectionName)]
public sealed class CoverageRepositoryMongoFilterTests(MongoRunnerFixture mongo) : IAsyncLifetime
{
    private const string Tenant = "t1";
    private IMongoDatabase _db = null!;

    public Task InitializeAsync()
    {
        _db = mongo.CreateDatabase("coverage_filters");
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => mongo.DropDatabaseAsync(_db);

    private CoverageRepositoryMongo Repo() => new(_db);

    private static DateTime D(int y, int m, int d) => new(y, m, d, 0, 0, 0, DateTimeKind.Utc);

    private Task<Coverage> SeedAsync(
        string id,
        CoverageStatus status = CoverageStatus.Active,
        DateTime? termination = null,
        string? line = null,
        string tenant = Tenant,
        string member = "M1") =>
        Repo().CreateAsync(new Coverage
        {
            Id = id,
            TenantId = tenant,
            MemberId = member,
            GroupNumber = "G1",
            PlanId = "P",
            Status = status,
            EffectiveDate = D(2025, 1, 1),
            TerminationDate = termination,
            InsuranceLineCode = line,
        });

    [Fact]
    public async Task ActiveOnDate_WithInsuranceLine_ReturnsOnlyThatLine_WithoutItEveryLine()
    {
        await SeedAsync("hlt", line: "HLT");
        await SeedAsync("den", line: "DEN");

        (await Repo().GetActiveCoverageByMemberIdAsync(Tenant, "M1", D(2025, 3, 15), "DEN"))
            .Select(c => c.Id).Should().Equal("den");
        (await Repo().GetActiveCoverageByMemberIdAsync(Tenant, "M1", D(2025, 3, 15)))
            .Select(c => c.Id).Should().BeEquivalentTo("hlt", "den");
    }

    [Fact]
    public async Task ActiveOnDate_IsScopedToTheTenantAndTheMember()
    {
        await SeedAsync("mine");
        await SeedAsync("other-tenant", tenant: "t2");
        await SeedAsync("other-member", member: "M2");

        (await Repo().GetActiveCoverageByMemberIdAsync(Tenant, "M1", D(2025, 3, 15)))
            .Select(c => c.Id).Should().Equal("mine");
        (await Repo().GetActiveCoverageByMemberIdAsync("t2", "M1", D(2025, 3, 15)))
            .Select(c => c.Id).Should().Equal("other-tenant");
    }

    [Fact]
    public async Task Search_NotActiveOnly_ReturnsCoverageWhateverItsStatusOrTerminationDate()
    {
        await SeedAsync("open");
        await SeedAsync("term-passed", termination: DateTime.UtcNow.Date.AddDays(-1));
        await SeedAsync("terminated", CoverageStatus.Terminated, D(2025, 6, 30));
        await SeedAsync("suspended", CoverageStatus.Suspended);

        var (items, _) = await Repo().SearchAsync(Tenant, groupNumber: "G1", pageSize: 100);

        items.Select(c => c.Id).Should().BeEquivalentTo("open", "term-passed", "terminated", "suspended");
    }

    [Fact]
    public async Task SetStatus_AfterAConcurrentEdit_ConflictsThenRereadsAndKeepsThatEdit()
    {
        await SeedAsync("c1", termination: D(2025, 6, 30));
        var observed = (await Repo().GetByIdAsync(Tenant, "c1"))!;

        // A PCP change lands between the sweep's read and its write.
        var edited = (await Repo().GetByIdAsync(Tenant, "c1"))!;
        edited.PcpNpi = "1234567893";
        await Repo().UpdateAsync(edited);

        (await Repo().SetStatusAsync(observed, CoverageStatus.Terminated, "sweep"))
            .Should().BeFalse("the sweep must not apply a transition from a stale revision");

        var refreshed = (await Repo().GetByIdAsync(Tenant, "c1"))!;
        refreshed.Status.Should().Be(CoverageStatus.Active);
        refreshed.PcpNpi.Should().Be("1234567893");
        refreshed.Revision.Should().Be(edited.Revision);

        (await Repo().SetStatusAsync(refreshed, CoverageStatus.Terminated, "sweep")).Should().BeTrue();

        var stored = (await Repo().GetByIdAsync(Tenant, "c1"))!;
        stored.Status.Should().Be(CoverageStatus.Terminated);
        stored.PcpNpi.Should().Be("1234567893");
        stored.Revision.Should().BeGreaterThan(edited.Revision);
    }
}
