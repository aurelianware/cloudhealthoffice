using CloudHealthOffice.Testing.Mongo;
using CoverageService.Models;
using CoverageService.Repositories;
using CoverageService.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;

namespace CoverageService.Tests.Repositories;

/// <summary>
/// GetActiveCoverageByMemberIdAsync against a real mongod: a coverage is
/// returned for a service date inside its effective/termination span whatever
/// its current status (a terminated coverage was in force for its span),
/// except statuses that are not in force (Suspended). Pending — only the
/// auto-assigned "not yet effective" state — is in force from its effective date.
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
    public Task CobraFound_SuspendedNotFound() => RunAsync(
        new[]
        {
            Build("susp", CoverageStatus.Suspended, D(2025, 1, 1)),
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
            var observed = (await repo.GetByIdAsync(Tenant, "c1"))!;

            var wrongStatus = (await repo.GetByIdAsync(Tenant, "c1"))!;
            wrongStatus.Status = CoverageStatus.COBRA;
            (await repo.SetStatusAsync(wrongStatus, CoverageStatus.Terminated, "sweep")).Should().BeFalse();
            (await repo.GetByIdAsync(Tenant, "c1"))!.Status.Should().Be(CoverageStatus.Active);

            (await repo.SetStatusAsync(observed, CoverageStatus.Terminated, "sweep")).Should().BeTrue();
            var stored = (await repo.GetByIdAsync(Tenant, "c1"))!;
            stored.Status.Should().Be(CoverageStatus.Terminated);
            stored.LastUpdatedBy.Should().Be("sweep");
            stored.TerminationDate.Should().Be(D(2025, 6, 30));

            var otherTenant = (await repo.GetByIdAsync(Tenant, "c1"))!;
            otherTenant.TenantId = "t2";
            (await repo.SetStatusAsync(otherTenant, CoverageStatus.Active, "sweep"))
                .Should().BeFalse("another tenant's id never matches");
        });

    [Fact]
    public Task SetStatus_StaleSweepAfterReinstatement_DoesNotUndoIt() => RunAsync(
        // Active with an expired termination date: due to be terminated.
        new[] { Build("c1", CoverageStatus.Active, D(2025, 1, 1), D(2025, 6, 30)) },
        async repo =>
        {
            // The sweep read it...
            var observed = (await repo.GetByIdAsync(Tenant, "c1"))!;
            observed.DueStatusTransition(D(2025, 7, 1)).Should().Be(CoverageStatus.Terminated);

            // ...then /reinstate cleared the termination date, leaving Active.
            var reinstated = (await repo.GetByIdAsync(Tenant, "c1"))!;
            reinstated.Reinstate();
            await repo.UpdateAsync(reinstated);

            // The stale write must not store Terminated-with-no-date.
            (await repo.SetStatusAsync(observed, CoverageStatus.Terminated, "sweep")).Should().BeFalse();
            var stored = (await repo.GetByIdAsync(Tenant, "c1"))!;
            stored.Status.Should().Be(CoverageStatus.Active);
            stored.TerminationDate.Should().BeNull();
            (await repo.GetActiveCoverageByMemberIdAsync(Tenant, "M1", D(2025, 8, 1)))
                .Select(c => c.Id).Should().Equal("c1");
        });

    [Fact]
    public Task Sweep_ReinstatedBetweenItsReadAndWrite_LeavesTheReinstatementInPlace() => RunAsync(
        new[] { Build("c1", CoverageStatus.Active, D(2025, 1, 1), D(2025, 6, 30)) },
        async real =>
        {
            // A repository whose due-read is followed by a concurrent reinstatement.
            var repo = new Mock<ICoverageRepository>();
            repo.Setup(r => r.GetStatusTransitionsDueAsync(It.IsAny<DateTime>(), It.IsAny<int>()))
                .Returns(async (DateTime today, int max) =>
                {
                    var due = await real.GetStatusTransitionsDueAsync(today, max);
                    var current = (await real.GetByIdAsync(Tenant, "c1"))!;
                    if (current.TerminationDate is not null)
                    {
                        current.Reinstate();
                        await real.UpdateAsync(current);
                    }
                    return due;
                });
            repo.Setup(r => r.SetStatusAsync(It.IsAny<Coverage>(), It.IsAny<CoverageStatus>(), It.IsAny<string>()))
                .Returns((Coverage c, CoverageStatus s, string by) => real.SetStatusAsync(c, s, by));

            var changed = await new CoverageStatusSweepJob(
                    new ServiceCollection().AddSingleton(repo.Object).BuildServiceProvider(),
                    new CoverageStatusSweepOptions(),
                    new FixedClock(D(2025, 7, 1)),
                    NullLogger<CoverageStatusSweepJob>.Instance)
                .SweepOnceAsync();

            changed.Should().Be(0);
            var stored = (await real.GetByIdAsync(Tenant, "c1"))!;
            stored.Status.Should().Be(CoverageStatus.Active);
            stored.TerminationDate.Should().BeNull();
        });

    [Fact]
    public Task GetStatusTransitionsDue_SkipsSuspendedAndUnknownStatuses_SoTheyCannotStallABatch() => RunAsync(
        new[]
        {
            // Inserted first, so a query that returned them would fill a batch of 1.
            Build("suspended", CoverageStatus.Suspended, D(2025, 1, 1), D(2025, 6, 30)),
            Build("unknown", (CoverageStatus)99, D(2025, 1, 1), D(2025, 6, 30)),
            Build("active", CoverageStatus.Active, D(2025, 1, 1), D(2025, 6, 30)),
            Build("pending", CoverageStatus.Pending, D(2025, 1, 1), D(2025, 6, 30))
        },
        async repo =>
        {
            (await repo.GetStatusTransitionsDueAsync(D(2025, 7, 1), 100))
                .Select(c => c.Id).Should().BeEquivalentTo("active", "pending");

            var changed = await new CoverageStatusSweepJob(
                    new ServiceCollection().AddSingleton<ICoverageRepository>(repo).BuildServiceProvider(),
                    new CoverageStatusSweepOptions { BatchSize = 1 },
                    new FixedClock(D(2025, 7, 1)),
                    NullLogger<CoverageStatusSweepJob>.Instance)
                .SweepOnceAsync();

            changed.Should().Be(2);
            (await repo.GetByIdAsync(Tenant, "suspended"))!.Status.Should().Be(CoverageStatus.Suspended);
            (await repo.GetByIdAsync(Tenant, "unknown"))!.Status.Should().Be((CoverageStatus)99);
            (await repo.GetActiveCoverageByMemberIdAsync(Tenant, "M1", D(2025, 3, 15)))
                .Select(c => c.Id).Should().BeEquivalentTo(new[] { "active", "pending" },
                    "a payment hold stays ineligible after its termination date passes");
        });

    [Fact]
    public Task CurrentlyActiveReads_ExcludeEffectiveCobraPending_BeforeAndAfterTheSweep() => RunAsync(
        new[]
        {
            WithPcp(WithCobra(Build("cobra-pending", CoverageStatus.Pending, DateTime.UtcNow.Date.AddDays(-2)))),
            WithPcp(Build("pending", CoverageStatus.Pending, DateTime.UtcNow.Date.AddDays(-2)))
        },
        async repo =>
        {
            async Task AssertActiveAsync()
            {
                var (items, _) = await repo.SearchAsync(Tenant, memberId: "M1", activeOnly: true, pageSize: 100);
                items.Select(c => c.Id).Should().Equal("pending");
                (await repo.GetByPcpNpiAsync(Tenant, "1234567893", CoverageStatus.Active))
                    .Select(c => c.Id).Should().Equal("pending");
            }

            await AssertActiveAsync();
            (await SweepAsync(repo, DateTime.UtcNow.Date)).Should().Be(2);
            (await repo.GetByIdAsync(Tenant, "cobra-pending"))!.Status.Should().Be(CoverageStatus.COBRA);
            await AssertActiveAsync();
        });

    private static Coverage WithCobra(Coverage c)
    {
        c.IsCOBRA = true;
        return c;
    }

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
    private sealed class FixedClock(DateTime today) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(today.AddHours(3), TimeSpan.Zero);
    }

    private static Task<int> SweepAsync(CoverageRepositoryMongo repo, DateTime today) =>
        new CoverageStatusSweepJob(
                new ServiceCollection().AddSingleton<ICoverageRepository>(repo).BuildServiceProvider(),
                new CoverageStatusSweepOptions(),
                new FixedClock(today),
                NullLogger<CoverageStatusSweepJob>.Instance)
            .SweepOnceAsync();

    [Fact]
    public Task FutureDatedAdd_IsEligibleOnItsEffectiveDate_WithoutTheSweep_NotTheDayBefore() => RunAsync(
        // As CoverageController.CreateCoverage stores an add ahead of its effective date.
        new[] { Build("future-add", CoverageStatus.Pending, D(2026, 1, 1)) },
        async repo =>
        {
            // The sweep has never run: still stored Pending.
            (await repo.GetByIdAsync(Tenant, "future-add"))!.Status.Should().Be(CoverageStatus.Pending);
            (await repo.GetActiveCoverageByMemberIdAsync(Tenant, "M1", D(2025, 12, 31))).Should().BeEmpty();
            (await repo.GetActiveCoverageByMemberIdAsync(Tenant, "M1", D(2026, 1, 1)))
                .Select(c => c.Id).Should().Equal("future-add");
        });

    [Fact]
    public Task FutureDatedAdd_SweepPromotesToActive_EligibilityUnchanged() => RunAsync(
        new[] { Build("future-add", CoverageStatus.Pending, D(2026, 1, 1)) },
        async repo =>
        {
            (await SweepAsync(repo, D(2025, 12, 31))).Should().Be(0);

            // On the effective date the sweep effectuates it (status hygiene).
            (await SweepAsync(repo, D(2026, 1, 1))).Should().Be(1);
            (await repo.GetByIdAsync(Tenant, "future-add"))!.Status.Should().Be(CoverageStatus.Active);
            (await repo.GetActiveCoverageByMemberIdAsync(Tenant, "M1", D(2026, 1, 1)))
                .Select(c => c.Id).Should().Equal("future-add");
            (await repo.GetActiveCoverageByMemberIdAsync(Tenant, "M1", D(2026, 3, 15)))
                .Select(c => c.Id).Should().Equal("future-add");
            (await repo.GetActiveCoverageByMemberIdAsync(Tenant, "M1", D(2025, 12, 31))).Should().BeEmpty();

            // Replay: nothing left to do.
            (await SweepAsync(repo, D(2026, 1, 1))).Should().Be(0);
        });

    [Fact]
    public Task CurrentlyActiveReads_ReportPendingAsActiveOnItsEffectiveDate_BeforeTheSweep() => RunAsync(
        new[]
        {
            Build("effective-today", CoverageStatus.Pending, DateTime.UtcNow.Date),
            Build("not-yet", CoverageStatus.Pending, DateTime.UtcNow.Date.AddDays(1)),
            WithPcp(Build("pcp-effective", CoverageStatus.Pending, DateTime.UtcNow.Date.AddDays(-2)))
        },
        async repo =>
        {
            var (items, _) = await repo.SearchAsync(Tenant, memberId: "M1", activeOnly: true, pageSize: 100);
            items.Select(c => c.Id).Should().BeEquivalentTo("effective-today", "pcp-effective");

            (await repo.GetByPcpNpiAsync(Tenant, "1234567893", CoverageStatus.Active))
                .Select(c => c.Id).Should().Equal("pcp-effective");
            (await repo.GetByPcpNpiAsync(Tenant, "1234567893", CoverageStatus.Pending))
                .Select(c => c.Id).Should().Equal(new[] { "pcp-effective" }, "a stored-status filter other than Active is unchanged");
        });

    private static Coverage WithPcp(Coverage c)
    {
        c.PcpNpi = "1234567893";
        return c;
    }
}
