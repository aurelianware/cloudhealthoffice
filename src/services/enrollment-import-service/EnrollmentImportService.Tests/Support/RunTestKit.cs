using System.Text.Json;
using CloudHealthOffice.Infrastructure.Edi.Files;
using CloudHealthOffice.Infrastructure.Edi.Interchange;
using EnrollmentImportService.Controllers;
using EnrollmentImportService.Models;
using EnrollmentImportService.Services;
using EnrollmentImportService.Services.Edi;
using EnrollmentImportService.Services.Runs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EnrollmentImportService.Tests.Support;

/// <summary>Tenant-scoped, copy-on-read/write import runs, like the Mongo repository.</summary>
public sealed class InMemoryEnrollmentImportRunRepository : IEnrollmentImportRunRepository
{
    private readonly object _gate = new();
    private readonly List<EnrollmentImportRun> _runs = new();

    public IReadOnlyList<EnrollmentImportRun> All { get { lock (_gate) return _runs.Select(Copy).ToList(); } }

    private static EnrollmentImportRun Copy(EnrollmentImportRun r) => JsonSerializer.Deserialize<EnrollmentImportRun>(JsonSerializer.Serialize(r))!;

    private EnrollmentImportRun? Find(string tenantId, string id) => _runs.FirstOrDefault(r => r.TenantId == tenantId && r.Id == id);

    public Task<EnrollmentImportRun> CreateAsync(EnrollmentImportRun run)
    {
        lock (_gate) _runs.Add(Copy(run));
        return Task.FromResult(run);
    }

    public Task<IReadOnlyList<EnrollmentImportRun>> ListRecentAsync(string tenantId, int limit = 100)
    {
        lock (_gate)
            return Task.FromResult<IReadOnlyList<EnrollmentImportRun>>(_runs.Where(r => r.TenantId == tenantId)
                .OrderByDescending(r => r.StartedAt).Take(limit).Select(Copy).ToList());
    }

    public Task<EnrollmentImportRun?> GetAsync(string tenantId, string id, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult(Find(tenantId, id) is { } r ? Copy(r) : null);
    }

    public Task<EnrollmentImportRun?> TryClaimAsync(string owner, DateTime now, DateTime leaseUntil, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var run = _runs.Where(r => r.Status == EnrollmentImportRunStatus.Queued
                                       || (r.Status == EnrollmentImportRunStatus.Running && r.LeaseExpiresAt < now))
                .OrderBy(r => r.QueuedAt).FirstOrDefault();
            if (run is null) return Task.FromResult<EnrollmentImportRun?>(null);
            run.Status = EnrollmentImportRunStatus.Running;
            run.LeaseOwner = owner;
            run.LeaseExpiresAt = leaseUntil;
            run.Attempts++;
            return Task.FromResult<EnrollmentImportRun?>(Copy(run));
        }
    }

    public Task<bool> RenewLeaseAsync(string tenantId, string id, string owner, DateTime leaseUntil, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var run = Find(tenantId, id);
            if (run is null || run.LeaseOwner != owner || run.Status != EnrollmentImportRunStatus.Running) return Task.FromResult(false);
            run.LeaseExpiresAt = leaseUntil;
            return Task.FromResult(true);
        }
    }

    public Task SetExpectedUnitsAsync(string tenantId, string id, int expectedUnits, CancellationToken ct = default)
    {
        lock (_gate) if (Find(tenantId, id) is { } run) run.ExpectedUnits = expectedUnits;
        return Task.CompletedTask;
    }

    public Task SetProgressAsync(string tenantId, string id, int dispatchedThrough, int completedUnits, int failedUnits, CancellationToken ct = default)
    {
        lock (_gate)
            if (Find(tenantId, id) is { } run)
                (run.DispatchedThrough, run.CompletedUnits, run.FailedUnits) = (dispatchedThrough, completedUnits, failedUnits);
        return Task.CompletedTask;
    }

    public Task AddUnitResultAsync(string tenantId, string id, ImportResult r, bool failed, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (Find(tenantId, id) is not { } run) return Task.CompletedTask;
            if (failed) run.FailedUnits++; else run.CompletedUnits++;
            run.SuccessCount += r.SuccessCount;
            run.FailedCount += r.FailedCount;
            run.SkippedCount += r.SkippedCount;
            run.MembersCreated += r.MembersCreated;
            run.DependentsCreated += r.DependentsCreated;
            run.DependentsFailed += r.DependentsFailed;
            run.Errors.AddRange(r.Errors.Take(Math.Max(0, EnrollmentImportRunRepository.MaxErrors - run.Errors.Count)));
        }
        return Task.CompletedTask;
    }

    public Task<bool> TryCompleteAsync(string tenantId, string id, DateTime at, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (Find(tenantId, id) is not { Status: EnrollmentImportRunStatus.Running } run) return Task.FromResult(false);
            run.Status = EnrollmentImportRunStatus.Completed;
            run.CompletedAt = at;
            run.LeaseOwner = null;
            run.LeaseExpiresAt = null;
            return Task.FromResult(true);
        }
    }

    public Task<bool> TryFailAsync(string tenantId, string id, string kind, string message, DateTime at, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (Find(tenantId, id) is not { } run || EnrollmentImportRunStatus.IsFinished(run.Status)) return Task.FromResult(false);
            run.Status = EnrollmentImportRunStatus.Failed;
            run.FailureKind = kind;
            run.FailureMessage = message;
            run.CompletedAt = at;
            return Task.FromResult(true);
        }
    }

    /// <summary>A worker that stopped mid-run: its lease has run out.</summary>
    public void ExpireLease(string id)
    {
        lock (_gate)
        {
            var run = _runs.Single(r => r.Id == id);
            run.Status = EnrollmentImportRunStatus.Running;
            run.LeaseExpiresAt = DateTime.UtcNow.AddMinutes(-1);
        }
    }
}

public sealed class InMemoryEnrollmentImportUnitStore : IEnrollmentImportUnitStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, EnrollmentImportUnitRecord> _units = new();

    public IReadOnlyList<EnrollmentImportUnitRecord> All { get { lock (_gate) return _units.Values.ToList(); } }

    public Task<bool> TryBeginAsync(string tenantId, string runId, int sequence, string filePosition, DateTime at, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var id = EnrollmentImportUnitRecord.IdFor(runId, sequence);
            if (_units.ContainsKey(id)) return Task.FromResult(false);
            _units[id] = new EnrollmentImportUnitRecord { Id = id, TenantId = tenantId, RunId = runId, Sequence = sequence, FilePosition = filePosition, StartedAt = at };
            return Task.FromResult(true);
        }
    }

    public Task FinishAsync(string tenantId, string runId, int sequence, bool failed, string? error, DateTime at, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var unit = _units[EnrollmentImportUnitRecord.IdFor(runId, sequence)];
            unit.Status = failed ? EnrollmentImportUnitStatus.Failed : EnrollmentImportUnitStatus.Done;
            unit.Error = error;
            unit.FinishedAt = at;
        }
        return Task.CompletedTask;
    }

    public Task<int> CountAsync(string tenantId, string runId, string status, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult(_units.Values.Count(u => u.TenantId == tenantId && u.RunId == runId && u.Status == status));
    }

    public Task<int> FailInterruptedAsync(string tenantId, string runId, DateTime at, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var interrupted = _units.Values.Where(u => u.TenantId == tenantId && u.RunId == runId && u.Status == EnrollmentImportUnitStatus.Applying).ToList();
            foreach (var unit in interrupted)
            {
                unit.Status = EnrollmentImportUnitStatus.Failed;
                unit.Error = EnrollmentImportUnitStore.InterruptedError;
            }
            return Task.FromResult(interrupted.Count);
        }
    }

    /// <summary>A unit a stopped worker left half-applied.</summary>
    public void LeaveApplying(string tenantId, string runId, int sequence) =>
        TryBeginAsync(tenantId, runId, sequence, "INS ?", DateTime.UtcNow).GetAwaiter().GetResult();
}

/// <summary>Runs a scheduled run straight away, so an upload's wait sees it finished.</summary>
public sealed class InlineRunScheduler(IServiceProvider services) : IEnrollmentImportRunScheduler
{
    public bool Paused { get; set; }

    public async Task ScheduleAsync(EnrollmentImportRun run, CancellationToken ct = default)
    {
        if (!Paused) await RunPendingAsync(ct);
    }

    public async Task RunPendingAsync(CancellationToken ct = default)
    {
        using var scope = services.CreateScope();
        var runs = scope.ServiceProvider.GetRequiredService<IEnrollmentImportRunRepository>();
        var processor = scope.ServiceProvider.GetRequiredService<EnrollmentImportRunProcessor>();
        while (await runs.TryClaimAsync("test-worker", DateTime.UtcNow, processor.LeaseUntil(), ct) is { } claimed)
            await processor.ProcessAsync(claimed, "test-worker", ct);
    }
}

/// <summary>The raw 834 path wired end to end, with the import service and parser replaceable.</summary>
public sealed class RunTestKit
{
    public InMemoryEnrollmentImportRunRepository Runs { get; } = new();
    public InMemoryEnrollmentImportUnitStore Units { get; } = new();
    public InMemoryRawEdiFileStore RawFiles { get; } = new();
    public InMemoryX12InterchangeStore Interchanges { get; } = new();
    public InMemoryEdiFileStore Files { get; } = new();
    public EnrollmentImportRunOptions Options { get; } = new() { WaitSeconds = 0, ChunkSize = 2 };
    public ServiceProvider Services { get; }

    public RunTestKit(IEnrollmentImportService import, IStreaming834Parser? parser = null, bool envelopeGate = true, int lanes = 1)
    {
        Options.Lanes = lanes;
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(Options));
        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(new X12InterchangeOptions { Enabled = envelopeGate }));
        services.AddSingleton(import);
        services.AddSingleton<IEnrollmentImportRunRepository>(Runs);
        services.AddSingleton<IEnrollmentImportUnitStore>(Units);
        services.AddSingleton<IRawEdiFileStore>(RawFiles);
        services.AddSingleton<IX12InterchangeStore>(Interchanges);
        services.AddSingleton<IEdiFileStore>(Files);
        services.AddScoped<IEdiFileRecorder>(_ => new EdiFileRecorder(Files, NullLogger<EdiFileRecorder>.Instance));
        services.AddScoped<IX12InterchangeIntake>(_ => new X12InterchangeIntake(Interchanges,
            Microsoft.Extensions.Options.Options.Create(new X12InterchangeOptions()), NullLogger<X12InterchangeIntake>.Instance));
        services.AddSingleton(parser ?? new Streaming834Parser());
        services.AddScoped<EnrollmentRunFinisher>();
        services.AddSingleton<IEnrollmentUnitApplier, EnrollmentUnitApplier>();
        services.AddSingleton<IEnrollmentUnitDispatcher, InProcessEnrollmentUnitDispatcher>();
        services.AddScoped<EnrollmentImportRunProcessor>();
        services.AddSingleton<InlineRunScheduler>();
        services.AddSingleton<IEnrollmentImportRunScheduler>(sp => sp.GetRequiredService<InlineRunScheduler>());
        services.AddScoped<Raw834Intake>();
        Services = services.BuildServiceProvider();
    }

    public InlineRunScheduler Scheduler => Services.GetRequiredService<InlineRunScheduler>();

    public EnrollmentController Controller(string tenantId = "t1")
    {
        var scope = Services.CreateScope();
        return new EnrollmentController(
            Services.GetRequiredService<IEnrollmentImportService>(), Moq.Mock.Of<IEnrollment834EdiParser>(),
            Moq.Mock.Of<IPlanCodeGapReportService>(), Runs, new TestActor(tenantId: tenantId),
            NullLogger<EnrollmentController>.Instance,
            raw834: scope.ServiceProvider.GetRequiredService<Raw834Intake>(),
            streamingParser: Services.GetRequiredService<IStreaming834Parser>());
    }
}
