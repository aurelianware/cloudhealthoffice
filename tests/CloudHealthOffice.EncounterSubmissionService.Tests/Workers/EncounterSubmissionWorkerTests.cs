using EncounterSubmissionService.Models;
using EncounterSubmissionService.Services;
using EncounterSubmissionService.StateRoutes;
using EncounterSubmissionService.Workers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using static CloudHealthOffice.EncounterSubmissionService.Tests.StateRoutes.StateRouteTestData;

namespace CloudHealthOffice.EncounterSubmissionService.Tests.Workers;

/// <summary>
/// One worker cycle: Florida deadline warnings and urgent FMMIS batches, then the
/// Texas / California batches. Kafka and blob staging are unconfigured, so the
/// cycle runs entirely against the service mocks.
/// </summary>
public class EncounterSubmissionWorkerTests
{
    private readonly Mock<IEncounterSubmissionService> _florida = new();
    private readonly Mock<IStateEncounterBatchService> _state = new();
    private IStateEncounterRouteRegistry _registry = StateEncounterRouteRegistry.FloridaOnly();
    private readonly Dictionary<string, string?> _config = new();

    public EncounterSubmissionWorkerTests()
    {
        _florida.Setup(s => s.GetApproachingDeadlineAsync(It.IsAny<int>())).ReturnsAsync(Array.Empty<EncounterSubmission>());
        _florida.Setup(s => s.BuildFmmisSubmissionBatchAsync(It.IsAny<IEnumerable<EncounterSubmission>>(), It.IsAny<string>()))
            .ReturnsAsync((IEnumerable<EncounterSubmission> subs, string t) => new FmmisSubmissionFileDto
            {
                BatchId = "batch-" + t, FileName = $"FMMIS_{t}.json", TransactionCount = subs.Count()
            });
        _state.Setup(s => s.GetBatchableAsync(It.IsAny<string>(), It.IsAny<int>()))
            .ReturnsAsync(Array.Empty<EncounterSubmission>());
        _state.Setup(s => s.BuildBatchAsync(It.IsAny<StateEncounterRoute>(), It.IsAny<IReadOnlyList<EncounterSubmission>>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((StateEncounterRoute r, IReadOnlyList<EncounterSubmission> subs, string t, CancellationToken _) =>
                new StateEncounterBatchResult
                {
                    BatchId = "b", StateCode = r.StateCode,
                    Files = { new StateEncounterFile { TenantId = t, FileName = "f.x12", Content = "ISA~" } },
                    BatchedSubmissionIds = subs.Select(s => s.Id).ToList()
                });
    }

    private EncounterSubmissionWorker Worker()
    {
        var services = new ServiceCollection()
            .AddSingleton(_florida.Object)
            .AddSingleton(_state.Object)
            .AddSingleton(_registry)
            .BuildServiceProvider();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(_config).Build();
        return new EncounterSubmissionWorker(services, NullLogger<EncounterSubmissionWorker>.Instance, configuration);
    }

    private static EncounterSubmission Sub(string id, string tenant = "tenant-1", string? state = "FL",
        double hoursToDeadline = 24, EncounterSubmissionStatus status = EncounterSubmissionStatus.Pending) => new()
    {
        Id = id, ClaimId = "claim-" + id, TenantId = tenant, StateCode = state!,
        SubmissionDeadline = DateTime.UtcNow.AddHours(hoursToDeadline), Status = status
    };

    // ── Florida deadline warnings ──────────────────────────────────────

    [Fact]
    public async Task OnlyPendingSubmissions_AreFlaggedAsDeadlineWarning()
    {
        var pending = Sub("1", hoursToDeadline: 120);
        var alreadyWarned = Sub("2", hoursToDeadline: 120, status: EncounterSubmissionStatus.DeadlineWarning);
        _florida.Setup(s => s.GetApproachingDeadlineAsync(7)).ReturnsAsync(new[] { pending, alreadyWarned });

        await Worker().RunCycleAsync();

        _florida.Verify(s => s.FlagDeadlineWarningAsync(pending), Times.Once);
        _florida.Verify(s => s.FlagDeadlineWarningAsync(alreadyWarned), Times.Never);
    }

    [Fact]
    public async Task WarningWindow_ComesFromConfiguration()
    {
        _config["Worker:DeadlineWarningDays"] = "10";

        await Worker().RunCycleAsync();

        _florida.Verify(s => s.GetApproachingDeadlineAsync(10), Times.Once);
    }

    // ── Florida urgent batches ─────────────────────────────────────────

    [Fact]
    public async Task UrgentFloridaSubmissions_AreBatchedPerTenant()
    {
        var a1 = Sub("a1", "tenant-a");
        var a2 = Sub("a2", "tenant-a", state: null, status: EncounterSubmissionStatus.DeadlineWarning);
        var b1 = Sub("b1", "tenant-b", state: "fl");
        _florida.Setup(s => s.GetApproachingDeadlineAsync(It.IsAny<int>())).ReturnsAsync(new[] { a1, a2, b1 });

        await Worker().RunCycleAsync();

        _florida.Verify(s => s.BuildFmmisSubmissionBatchAsync(
            It.Is<IEnumerable<EncounterSubmission>>(x => x.Select(s => s.Id).SequenceEqual(new[] { "a1", "a2" })), "tenant-a"), Times.Once);
        _florida.Verify(s => s.BuildFmmisSubmissionBatchAsync(
            It.Is<IEnumerable<EncounterSubmission>>(x => x.Count() == 1 && x.First().Id == "b1"), "tenant-b"), Times.Once);
    }

    [Fact]
    public async Task SubmissionsNotYetUrgent_OrNotFlorida_AreNotFmmisBatched()
    {
        var later = Sub("later", hoursToDeadline: 120);
        var texas = Sub("tx", state: "TX");
        var california = Sub("ca", state: "CA");
        _florida.Setup(s => s.GetApproachingDeadlineAsync(It.IsAny<int>())).ReturnsAsync(new[] { later, texas, california });

        await Worker().RunCycleAsync();

        _florida.Verify(s => s.BuildFmmisSubmissionBatchAsync(It.IsAny<IEnumerable<EncounterSubmission>>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task UrgentWindow_ComesFromConfiguration()
    {
        _config["Worker:UrgentBatchHours"] = "200";
        _florida.Setup(s => s.GetApproachingDeadlineAsync(It.IsAny<int>())).ReturnsAsync(new[] { Sub("1", hoursToDeadline: 120) });

        await Worker().RunCycleAsync();

        _florida.Verify(s => s.BuildFmmisSubmissionBatchAsync(It.IsAny<IEnumerable<EncounterSubmission>>(), "tenant-1"), Times.Once);
    }

    [Fact]
    public async Task OneTenantsBatchFailure_DoesNotStopTheOthers_OrTheStateRoutes()
    {
        _registry = Registry(tx: TexasOptions());
        _florida.Setup(s => s.GetApproachingDeadlineAsync(It.IsAny<int>()))
            .ReturnsAsync(new[] { Sub("a1", "tenant-a"), Sub("b1", "tenant-b") });
        _florida.Setup(s => s.BuildFmmisSubmissionBatchAsync(It.IsAny<IEnumerable<EncounterSubmission>>(), "tenant-a"))
            .ThrowsAsync(new HttpRequestException("claims-service down"));
        _state.Setup(s => s.GetBatchableAsync("TX", It.IsAny<int>())).ReturnsAsync(new[] { Sub("t1", state: "TX") });

        await Worker().RunCycleAsync();

        _florida.Verify(s => s.BuildFmmisSubmissionBatchAsync(It.IsAny<IEnumerable<EncounterSubmission>>(), "tenant-b"), Times.Once);
        _state.Verify(s => s.BuildBatchAsync(StateEncounterRoute.Texas, It.IsAny<IReadOnlyList<EncounterSubmission>>(),
            "tenant-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Texas / California ─────────────────────────────────────────────

    [Fact]
    public async Task StateRoutes_NotEnabled_AreNotRead()
    {
        await Worker().RunStateRoutesAsync(_registry, _state.Object);

        _state.Verify(s => s.GetBatchableAsync(It.IsAny<string>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task FloridaIsNeverBatchedAsX12()
    {
        _registry = Registry(tx: TexasOptions(), ca: CaliforniaOptions());

        await Worker().RunStateRoutesAsync(_registry, _state.Object);

        _state.Verify(s => s.GetBatchableAsync("TX", It.IsAny<int>()), Times.Once);
        _state.Verify(s => s.GetBatchableAsync("CA", It.IsAny<int>()), Times.Once);
        _state.Verify(s => s.GetBatchableAsync("FL", It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task EnabledRoute_BatchesEachEnrolledTenantSeparately()
    {
        var tx = TexasOptions();
        tx.Tenants["tenant-2"] = TexasOptions().Tenants["tenant-1"];
        _registry = Registry(tx: tx);
        _state.Setup(s => s.GetBatchableAsync("TX", 50_000)).ReturnsAsync(new[]
        {
            Sub("1", "tenant-1", "TX"), Sub("2", "tenant-2", "TX"), Sub("3", "tenant-1", "TX")
        });

        await Worker().RunStateRoutesAsync(_registry, _state.Object);

        _state.Verify(s => s.BuildBatchAsync(StateEncounterRoute.Texas,
            It.Is<IReadOnlyList<EncounterSubmission>>(x => x.Select(s => s.Id).SequenceEqual(new[] { "1", "3" })),
            "tenant-1", It.IsAny<CancellationToken>()), Times.Once);
        _state.Verify(s => s.BuildBatchAsync(StateEncounterRoute.Texas,
            It.Is<IReadOnlyList<EncounterSubmission>>(x => x.Count == 1 && x[0].Id == "2"),
            "tenant-2", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TenantNotEnrolledOnTheRoute_IsLeftWaiting()
    {
        _registry = Registry(tx: TexasOptions());
        _state.Setup(s => s.GetBatchableAsync("TX", It.IsAny<int>())).ReturnsAsync(new[] { Sub("1", "tenant-unknown", "TX") });

        await Worker().RunStateRoutesAsync(_registry, _state.Object);

        _state.Verify(s => s.BuildBatchAsync(It.IsAny<StateEncounterRoute>(), It.IsAny<IReadOnlyList<EncounterSubmission>>(),
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task LargeBacklogs_AreSplitIntoBatchesOfTheConfiguredSize()
    {
        _config["Worker:StateBatchMaxEncounters"] = "2";
        _registry = Registry(tx: TexasOptions());
        _state.Setup(s => s.GetBatchableAsync("TX", 20))
            .ReturnsAsync(Enumerable.Range(1, 5).Select(i => Sub(i.ToString(), state: "TX")).ToArray());

        await Worker().RunStateRoutesAsync(_registry, _state.Object);

        _state.Verify(s => s.BuildBatchAsync(StateEncounterRoute.Texas, It.Is<IReadOnlyList<EncounterSubmission>>(x => x.Count == 2),
            "tenant-1", It.IsAny<CancellationToken>()), Times.Exactly(2));
        _state.Verify(s => s.BuildBatchAsync(StateEncounterRoute.Texas, It.Is<IReadOnlyList<EncounterSubmission>>(x => x.Count == 1),
            "tenant-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AFailedChunk_DoesNotStopTheNextChunk()
    {
        _config["Worker:StateBatchMaxEncounters"] = "1";
        _registry = Registry(tx: TexasOptions());
        _state.Setup(s => s.GetBatchableAsync("TX", It.IsAny<int>()))
            .ReturnsAsync(new[] { Sub("1", state: "TX"), Sub("2", state: "TX") });
        _state.Setup(s => s.BuildBatchAsync(StateEncounterRoute.Texas,
                It.Is<IReadOnlyList<EncounterSubmission>>(x => x[0].Id == "1"), "tenant-1", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("member-service down"));

        await Worker().RunStateRoutesAsync(_registry, _state.Object);

        _state.Verify(s => s.BuildBatchAsync(StateEncounterRoute.Texas,
            It.Is<IReadOnlyList<EncounterSubmission>>(x => x[0].Id == "2"), "tenant-1", It.IsAny<CancellationToken>()), Times.Once);
    }
}
