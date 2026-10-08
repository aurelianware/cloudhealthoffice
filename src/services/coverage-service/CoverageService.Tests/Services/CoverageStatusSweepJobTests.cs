using CoverageService.Models;
using CoverageService.Repositories;
using CoverageService.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace CoverageService.Tests.Services;

public class CoverageStatusSweepJobTests
{
    private static readonly DateTimeOffset Now = new(2025, 7, 1, 2, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static (CoverageStatusSweepJob Job, Mock<ICoverageRepository> Repo) Build(int batchSize = 500)
    {
        var repo = new Mock<ICoverageRepository>();
        var services = new ServiceCollection().AddSingleton(repo.Object).BuildServiceProvider();
        var job = new CoverageStatusSweepJob(
            services,
            new CoverageStatusSweepOptions { BatchSize = batchSize },
            new FixedClock(Now),
            NullLogger<CoverageStatusSweepJob>.Instance);
        return (job, repo);
    }

    private static Coverage Cov(string id, CoverageStatus status, DateTime? termination, string tenant = "t1") => new()
    {
        Id = id,
        TenantId = tenant,
        MemberId = "M1",
        GroupNumber = "G",
        PlanId = "P",
        Status = status,
        EffectiveDate = new DateTime(2025, 1, 1),
        TerminationDate = termination
    };

    [Fact]
    public async Task Sweep_FlipsCoveragesWhoseTerminationDateIsReached_ToTerminated()
    {
        var (job, repo) = Build();
        repo.Setup(r => r.GetStatusTransitionsDueAsync(new DateTime(2025, 7, 1), 500))
            .ReturnsAsync(new List<Coverage>
            {
                Cov("a", CoverageStatus.Active, new DateTime(2025, 7, 1)),
                Cov("b", CoverageStatus.COBRA, new DateTime(2025, 6, 15), "t2")
            });
        repo.Setup(r => r.SetStatusAsync(It.IsAny<Coverage>(), It.IsAny<CoverageStatus>(), It.IsAny<string>()))
            .ReturnsAsync(true);

        (await job.SweepOnceAsync()).Should().Be(2);

        repo.Verify(r => r.SetStatusAsync(It.Is<Coverage>(c => c.TenantId == "t1" && c.Id == "a" && c.Status == CoverageStatus.Active), CoverageStatus.Terminated,
            CoverageStatusSweepJob.Actor), Times.Once);
        repo.Verify(r => r.SetStatusAsync(It.Is<Coverage>(c => c.TenantId == "t2" && c.Id == "b" && c.Status == CoverageStatus.COBRA), CoverageStatus.Terminated,
            CoverageStatusSweepJob.Actor), Times.Once);
        // Status only: never a whole-document replace that could clobber a concurrent edit.
        repo.Verify(r => r.UpdateAsync(It.IsAny<Coverage>()), Times.Never);
    }

    [Fact]
    public async Task Sweep_PromotesPendingWhoseEffectiveDateArrived_ToActive()
    {
        var (job, repo) = Build();
        var pending = Cov("p", CoverageStatus.Pending, null);
        pending.EffectiveDate = new DateTime(2025, 7, 1);
        repo.Setup(r => r.GetStatusTransitionsDueAsync(It.IsAny<DateTime>(), It.IsAny<int>()))
            .ReturnsAsync(new List<Coverage> { pending });
        repo.Setup(r => r.SetStatusAsync(It.IsAny<Coverage>(), It.IsAny<CoverageStatus>(), It.IsAny<string>()))
            .ReturnsAsync(true);

        (await job.SweepOnceAsync()).Should().Be(1);
        repo.Verify(r => r.SetStatusAsync(It.Is<Coverage>(c => c.TenantId == "t1" && c.Id == "p" && c.Status == CoverageStatus.Pending), CoverageStatus.Active,
            CoverageStatusSweepJob.Actor), Times.Once);
    }

    [Fact]
    public async Task Sweep_SkipsRowsThatAreNotActuallyDue()
    {
        var (job, repo) = Build();
        repo.Setup(r => r.GetStatusTransitionsDueAsync(It.IsAny<DateTime>(), It.IsAny<int>()))
            .ReturnsAsync(new List<Coverage> { Cov("future", CoverageStatus.Active, new DateTime(2025, 7, 2)) });

        (await job.SweepOnceAsync()).Should().Be(0);
        repo.Verify(r => r.SetStatusAsync(It.IsAny<Coverage>(), It.IsAny<CoverageStatus>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Sweep_NeverAutoTerminatesSuspendedCoverage()
    {
        var (job, repo) = Build();
        repo.Setup(r => r.GetStatusTransitionsDueAsync(It.IsAny<DateTime>(), It.IsAny<int>()))
            .ReturnsAsync(new List<Coverage> { Cov("hold", CoverageStatus.Suspended, new DateTime(2025, 6, 1)) });

        (await job.SweepOnceAsync()).Should().Be(0);
        repo.Verify(r => r.SetStatusAsync(It.IsAny<Coverage>(), It.IsAny<CoverageStatus>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Sweep_PassesTheCoverageItRead_SoTheWriteIsConditionalOnItsDates()
    {
        var (job, repo) = Build();
        var due = Cov("a", CoverageStatus.Active, new DateTime(2025, 6, 30));
        repo.Setup(r => r.GetStatusTransitionsDueAsync(It.IsAny<DateTime>(), It.IsAny<int>()))
            .ReturnsAsync(new List<Coverage> { due });
        repo.Setup(r => r.SetStatusAsync(It.IsAny<Coverage>(), It.IsAny<CoverageStatus>(), It.IsAny<string>()))
            .ReturnsAsync(true);

        await job.SweepOnceAsync();
        repo.Verify(r => r.SetStatusAsync(It.Is<Coverage>(c => ReferenceEquals(c, due)
                && c.TerminationDate == new DateTime(2025, 6, 30)), CoverageStatus.Terminated, CoverageStatusSweepJob.Actor),
            Times.Once);
    }

    [Fact]
    public async Task Sweep_RepeatsFullBatchesUntilNoneAreLeft()
    {
        var (job, repo) = Build(batchSize: 2);
        repo.SetupSequence(r => r.GetStatusTransitionsDueAsync(It.IsAny<DateTime>(), 2))
            .ReturnsAsync(new List<Coverage>
            {
                Cov("a", CoverageStatus.Active, new DateTime(2025, 6, 1)),
                Cov("b", CoverageStatus.Active, new DateTime(2025, 6, 1))
            })
            .ReturnsAsync(new List<Coverage> { Cov("c", CoverageStatus.Active, new DateTime(2025, 6, 1)) });
        repo.Setup(r => r.SetStatusAsync(It.IsAny<Coverage>(), It.IsAny<CoverageStatus>(), It.IsAny<string>()))
            .ReturnsAsync(true);

        (await job.SweepOnceAsync()).Should().Be(3);
        repo.Verify(r => r.GetStatusTransitionsDueAsync(It.IsAny<DateTime>(), 2), Times.Exactly(2));
    }

    [Fact]
    public async Task Sweep_FullBatchWithNoProgress_StopsInsteadOfLooping()
    {
        // Every conditional update lost a race: the same rows would come back.
        var (job, repo) = Build(batchSize: 1);
        repo.Setup(r => r.GetStatusTransitionsDueAsync(It.IsAny<DateTime>(), 1))
            .ReturnsAsync(new List<Coverage> { Cov("a", CoverageStatus.Active, new DateTime(2025, 6, 1)) });
        repo.Setup(r => r.SetStatusAsync(It.IsAny<Coverage>(), It.IsAny<CoverageStatus>(), It.IsAny<string>()))
            .ReturnsAsync(false);

        (await job.SweepOnceAsync()).Should().Be(0);
        repo.Verify(r => r.GetStatusTransitionsDueAsync(It.IsAny<DateTime>(), 1), Times.Once);
    }

    [Fact]
    public async Task HostedService_Disabled_DoesNotSweep()
    {
        var repo = new Mock<ICoverageRepository>();
        var services = new ServiceCollection().AddSingleton(repo.Object).BuildServiceProvider();
        var job = new CoverageStatusSweepJob(
            services,
            new CoverageStatusSweepOptions { Enabled = false, StartupDelaySeconds = 0 },
            new FixedClock(Now),
            NullLogger<CoverageStatusSweepJob>.Instance);

        await job.StartAsync(CancellationToken.None);
        await job.ExecuteTask!;
        await job.StopAsync(CancellationToken.None);

        repo.Verify(r => r.GetStatusTransitionsDueAsync(It.IsAny<DateTime>(), It.IsAny<int>()), Times.Never);
    }
}
