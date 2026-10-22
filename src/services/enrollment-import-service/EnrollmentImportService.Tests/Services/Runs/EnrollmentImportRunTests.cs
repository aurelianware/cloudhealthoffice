using System.Collections.Concurrent;
using System.Text;
using CloudHealthOffice.Infrastructure.Edi.Files;
using EnrollmentImportService.Models;
using EnrollmentImportService.Services;
using EnrollmentImportService.Services.Edi;
using EnrollmentImportService.Services.Runs;
using EnrollmentImportService.Tests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace EnrollmentImportService.Tests.Services.Runs;

/// <summary>A raw 834 imported as a run of units: scatter, apply once, gather once, resume (edi-operations.md §11).</summary>
public class EnrollmentImportRunTests
{
    private const string Isa =
        "ISA*00*          *00*          *ZZ*SPONSOR01      *ZZ*CHO            *260115*1200*^*00501*000000701*0*P*:~";

    /// <summary><paramref name="families"/> subscribers (the even ones with a dependent), then a newborn sent alone.</summary>
    private static string File834(int families, string control = "000000701")
    {
        var b = new StringBuilder(Isa.Replace("000000701", control))
            .Append("GS*BE*SPONSOR01*CHO*20260115*1200*7*X*005010X220A1~ST*834*0001*005010X220A1~BGN*00*REF1*20260115*1200****2~");
        for (var i = 1; i <= families; i++)
        {
            b.Append($"INS*Y*18*021*28*A~REF*0F*SUB{i}~NM1*IL*1*DOE*P{i}~");
            if (i % 2 == 0) b.Append($"INS*N*19*021*28*A~REF*0F*SUB{i}~NM1*IL*1*DOE*K{i}~");
        }
        b.Append("INS*N*19*021*28*A~REF*0F*SUB-OLD~NM1*IL*1*DOE*BABY~SE*9*0001~GE*1*7~").Append($"IEA*1*{control}~");
        return b.ToString();
    }

    private static IFormFile Upload(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        return new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", "acme.834");
    }

    /// <summary>Records every batch it imports, in the order each lane applied them.</summary>
    private sealed class RecordingImport : IEnrollmentImportService
    {
        public ConcurrentQueue<Enrollment834> Applied { get; } = new();
        public Func<Enrollment834, Exception?> FailWith { get; set; } = _ => null;

        public async Task<ImportResult> ImportEnrollmentAsync(Enrollment834 enrollment, string tenantId)
        {
            await Task.Yield();
            if (FailWith(enrollment) is { } ex) throw ex;
            Applied.Enqueue(enrollment);
            return new ImportResult
            {
                SuccessCount = enrollment.Enrollments.Count + enrollment.DependentEnrollments.Count,
                MembersCreated = enrollment.Enrollments.Count,
            };
        }
    }

    [Fact]
    public async Task AFile_IsAppliedUnitByUnit_AcrossLanes_AndFinishedOnce()
    {
        var import = new RecordingImport();
        var kit = new RunTestKit(import, lanes: 3);

        var response = await kit.Controller().ImportRaw834(Upload(File834(7)));

        var result = Assert.IsType<ImportResult>(Assert.IsType<OkObjectResult>(response.Result).Value);
        result.SuccessCount.Should().Be(8, "seven subscribers and one newborn");
        var run = kit.Runs.All.Single();
        run.Status.Should().Be(EnrollmentImportRunStatus.Completed);
        run.ExpectedUnits.Should().Be(8);
        run.CompletedUnits.Should().Be(8);
        run.FailedUnits.Should().Be(0);
        run.DispatchedThrough.Should().Be(8);

        var applied = import.Applied.ToList();
        applied.Should().HaveCount(8);
        applied.Should().OnlyContain(b => b.BatchId == run.BatchId && b.EdiFileId == run.EdiFileId && !b.RecordRun);
        applied.SelectMany(b => b.Enrollments).Select(e => e.SubscriberId)
            .Should().BeEquivalentTo(Enumerable.Range(1, 7).Select(i => $"SUB{i}"));
        applied.Single(b => b.Enrollments.Any(e => e.SubscriberId == "SUB4")).SubscriberIndexOffset.Should().Be(3);
        applied.Single(b => b.DependentEnrollments.Count == 1).DependentIndexOffset.Should().Be(0);
        kit.Units.All.Should().HaveCount(8).And.OnlyContain(u => u.Status == EnrollmentImportUnitStatus.Done);

        var file = (await kit.Files.ListAsync("t1", new EdiFileQuery())).Single();
        file.Status.Should().Be(EdiFileStatus.Applied);
        file.AcceptedRecords.Should().Be(8);
        file.History.Count(h => h.Status == EdiFileStatus.Applied).Should().Be(1);
    }

    [Fact]
    public async Task ARedeliveredUnit_IsNeverAppliedTwice()
    {
        var import = new RecordingImport();
        var kit = new RunTestKit(import);
        await kit.Controller().ImportRaw834(Upload(File834(2)));
        var run = kit.Runs.All.Single();

        var applier = kit.Services.GetRequiredService<IEnrollmentUnitApplier>();
        await applier.ApplyAsync(new EnrollmentUnit
        {
            TenantId = "t1", RunId = run.Id, BatchId = run.BatchId, Sequence = 0, FilePosition = "INS 1", OrderingKey = "SUB1",
            Kind = EnrollmentUnitKind.Subscriber, Batch = new Enrollment834 { Enrollments = { new MemberEnrollment { SubscriberId = "SUB1" } } },
        });

        import.Applied.Should().HaveCount(3);
        kit.Runs.All.Single().CompletedUnits.Should().Be(3);
    }

    [Fact]
    public async Task UnitsOfOneSubscriber_ShareALane_SoTheyApplyInFileOrder()
    {
        var lanes = Enumerable.Range(0, 100).Select(_ => InProcessEnrollmentUnitDispatcher.LaneFor("t1\u001fSUB42", 8)).Distinct();
        lanes.Should().ContainSingle();
        Enumerable.Range(0, 200).Select(i => InProcessEnrollmentUnitDispatcher.LaneFor($"t1\u001fSUB{i}", 8)).Distinct()
            .Should().HaveCount(8, "subscribers spread over every lane");
        await Task.CompletedTask;
    }

    [Fact]
    public async Task AFailingUnit_IsCountedAndRecorded_AndTheRestStillApply()
    {
        var import = new RecordingImport { FailWith = b => b.Enrollments.Any(e => e.SubscriberId == "SUB2") ? new InvalidOperationException("boom") : null };
        var kit = new RunTestKit(import);

        var response = await kit.Controller().ImportRaw834(Upload(File834(3)));

        var result = Assert.IsType<ImportResult>(Assert.IsType<OkObjectResult>(response.Result).Value);
        result.Errors.Should().ContainSingle(e => e.StartsWith("INS 2:"));
        var run = kit.Runs.All.Single();
        (run.CompletedUnits, run.FailedUnits).Should().Be((3, 1));
        kit.Units.All.Single(u => u.Status == EnrollmentImportUnitStatus.Failed).FilePosition.Should().Be("INS 2");
    }

    [Fact]
    public async Task ARunWhoseWorkerStopped_ResumesFromItsCheckpoint_WithoutApplyingAnythingTwice()
    {
        var import = new RecordingImport();
        var kit = new RunTestKit(import);
        kit.Scheduler.Paused = true;
        var accepted = await kit.Controller().ImportRaw834(Upload(File834(5)));
        Assert.IsType<AcceptedResult>(accepted.Result);
        var run = kit.Runs.All.Single();

        // The first worker counted 6 units, applied units 0–1, and stopped while applying unit 2.
        await kit.Runs.SetExpectedUnitsAsync("t1", run.Id, 6);
        foreach (var seq in new[] { 0, 1 })
        {
            await kit.Units.TryBeginAsync("t1", run.Id, seq, $"INS {seq + 1}", DateTime.UtcNow);
            await kit.Units.FinishAsync("t1", run.Id, seq, false, null, DateTime.UtcNow);
        }
        kit.Units.LeaveApplying("t1", run.Id, 2);
        await kit.Runs.SetProgressAsync("t1", run.Id, 2, 2, 0);
        kit.Runs.ExpireLease(run.Id);

        await kit.Scheduler.RunPendingAsync();

        var after = kit.Runs.All.Single();
        after.Status.Should().Be(EnrollmentImportRunStatus.Completed);
        (after.CompletedUnits, after.FailedUnits).Should().Be((5, 1));
        import.Applied.SelectMany(b => b.Enrollments).Select(e => e.SubscriberId)
            .Should().BeEquivalentTo(["SUB4", "SUB5"], "units 0–1 were applied before, unit 2 may be partly applied");
        import.Applied.Should().Contain(b => b.DependentEnrollments.Count == 1);
        kit.Units.All.Single(u => u.Sequence == 2).Error.Should().Be($"INS 3: {EnrollmentImportUnitStore.InterruptedError}");
    }

    [Fact]
    public async Task AUnitThatThrows_OpensAnException_ForItsWholeChange_AndKeepsMemberDataOffTheRun()
    {
        var import = new RecordingImport { FailWith = b => b.Enrollments.Any(e => e.SubscriberId == "SUB2") ? new InvalidOperationException("SUB2 DOE exploded") : null };
        var kit = new RunTestKit(import);
        var controller = kit.Controller();

        await controller.ImportRaw834(Upload(File834(3)));

        var run = kit.Runs.All.Single();
        var exception = kit.Exceptions.All.Should().ContainSingle().Subject;
        exception.Id.Should().Be(EnrollmentUnitExceptions.IdFor("t1", run.Id, 1));
        exception.ReasonCode.Should().Be(EnrollmentRejectionReasons.UnexpectedError);
        exception.SourceKind.Should().Be(EnrollmentExceptionSourceKind.Subscriber);
        exception.Subscriber!.SubscriberId.Should().Be("SUB2");
        exception.Subscriber.Dependents.Should().ContainSingle(d => d.FirstName == "K2", "the subscriber with its dependents, as received");
        (exception.FilePosition, exception.BatchId, exception.EdiFileId, exception.SubscriberId, exception.FileName)
            .Should().Be(("INS 2", run.BatchId, run.EdiFileId, "SUB2", "acme.834"));

        var unit = kit.Units.All.Single(u => u.Status == EnrollmentImportUnitStatus.Failed);
        unit.ExceptionId.Should().Be(exception.Id);
        // Only the position and a fixed text: no member names or ids, no exception text.
        foreach (var text in run.Errors.Append(unit.Error!))
        {
            text.Should().Be($"INS 2: {EnrollmentImportUnitStore.FailedError}");
            text.Should().NotContain("SUB2").And.NotContain("DOE").And.NotContain("exploded");
        }

        var status = (EnrollmentImportRunStatusResponse)Assert.IsType<OkObjectResult>(await controller.GetImportRun(run.Id, CancellationToken.None)).Value!;
        status.FailedUnitDetails.Should().ContainSingle(f => f.FilePosition == "INS 2" && f.ExceptionId == exception.Id);

        // A redelivery of the failed unit neither applies it again nor opens a second exception.
        await kit.Services.GetRequiredService<IEnrollmentUnitApplier>().ApplyAsync(new EnrollmentUnit
        {
            TenantId = "t1", RunId = run.Id, BatchId = run.BatchId, Sequence = 1, FilePosition = "INS 2", OrderingKey = "SUB2",
            Kind = EnrollmentUnitKind.Subscriber, Batch = new Enrollment834 { Enrollments = { new MemberEnrollment { SubscriberId = "SUB2" } } },
        });
        kit.Exceptions.All.Should().ContainSingle();
    }

    [Fact]
    public async Task ReprocessingAThrownUnitsException_ReappliesItsChangeOnce()
    {
        var failures = 0;
        var import = new RecordingImport
        {
            FailWith = b => b.ReprocessExceptionId is null && b.Enrollments.Any(e => e.SubscriberId == "SUB2") && failures++ == 0
                ? new InvalidOperationException("boom") : null,
        };
        var kit = new RunTestKit(import);
        await kit.Controller().ImportRaw834(Upload(File834(3)));
        var exception = kit.Exceptions.All.Single();

        var service = new EnrollmentExceptionService(kit.Exceptions, import, TimeProvider.System);
        var result = await service.ReprocessAsync("t1", exception.Id, "specialist", null, null, "member-service is back");

        result.Outcome.Should().Be(ExceptionActionOutcome.Done);
        var reprocessed = import.Applied.Where(b => b.ReprocessExceptionId == exception.Id).Should().ContainSingle().Subject;
        reprocessed.Enrollments.Single().SubscriberId.Should().Be("SUB2");
        reprocessed.Enrollments.Single().Dependents.Should().ContainSingle(d => d.FirstName == "K2");
        reprocessed.BatchId.Should().Be($"{exception.BatchId}-RP1");
        import.Applied.SelectMany(b => b.Enrollments).Count(e => e.SubscriberId == "SUB2").Should().Be(1, "applied once: by the reprocess");
    }

    [Fact]
    public async Task AnInterruptedUnit_OpensAnException_WhenItsRunResumes()
    {
        var import = new RecordingImport();
        var kit = new RunTestKit(import);
        kit.Scheduler.Paused = true;
        await kit.Controller().ImportRaw834(Upload(File834(3)));
        var run = kit.Runs.All.Single();
        await kit.Runs.SetExpectedUnitsAsync("t1", run.Id, 4);
        kit.Units.LeaveApplying("t1", run.Id, 1);
        kit.Runs.ExpireLease(run.Id);

        await kit.Scheduler.RunPendingAsync();

        var exception = kit.Exceptions.All.Should().ContainSingle().Subject;
        exception.Id.Should().Be(EnrollmentUnitExceptions.IdFor("t1", run.Id, 1));
        exception.ReasonCode.Should().Be(EnrollmentRejectionReasons.UnitInterrupted);
        exception.Errors.Single().Message.Should().Contain("partly applied").And.Contain("before reprocessing");
        exception.Subscriber!.SubscriberId.Should().Be("SUB2");
        exception.Subscriber.Dependents.Should().ContainSingle();
        kit.Units.All.Single(u => u.Sequence == 1).ExceptionId.Should().Be(exception.Id);
        import.Applied.SelectMany(b => b.Enrollments).Select(e => e.SubscriberId).Should().BeEquivalentTo(["SUB1", "SUB3"]);
        kit.Runs.All.Single().Status.Should().Be(EnrollmentImportRunStatus.Completed);

        // A further resume opens nothing more.
        kit.Runs.ExpireLease(run.Id);
        await kit.Scheduler.RunPendingAsync();
        kit.Exceptions.All.Should().ContainSingle();
    }

    [Fact]
    public async Task TheGather_FinishesTheRunExactlyOnce()
    {
        var import = new RecordingImport();
        var kit = new RunTestKit(import);
        kit.Scheduler.Paused = true;
        await kit.Controller().ImportRaw834(Upload(File834(1)));
        var run = kit.Runs.All.Single();
        await kit.Runs.TryClaimAsync("w", DateTime.UtcNow, DateTime.UtcNow.AddMinutes(1));
        await kit.Runs.SetExpectedUnitsAsync("t1", run.Id, 2);
        await kit.Runs.SetProgressAsync("t1", run.Id, 2, 2, 0);

        var finishes = await Task.WhenAll(Enumerable.Range(0, 10).Select(async _ =>
        {
            using var scope = kit.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<EnrollmentRunFinisher>().TryFinishAsync("t1", run.Id);
        }));

        finishes.Count(f => f).Should().Be(1);
        var file = (await kit.Files.ListAsync("t1", new EdiFileQuery())).Single();
        file.History.Count(h => h.Status is EdiFileStatus.Applied or EdiFileStatus.Rejected).Should().Be(1);
    }

    [Fact]
    public async Task AnUploadThatOutlastsTheWait_Answers202_AndItsRunCanBeRead()
    {
        var kit = new RunTestKit(new RecordingImport());
        kit.Scheduler.Paused = true;
        var controller = kit.Controller();

        var response = await controller.ImportRaw834(Upload(File834(2)));

        var accepted = Assert.IsType<AcceptedResult>(response.Result);
        var body = Assert.IsType<EnrollmentImportRunStatusResponse>(accepted.Value);
        body.Status.Should().Be(EnrollmentImportRunStatus.Queued);
        body.EdiFileId.Should().NotBeNull();
        accepted.Location.Should().Be($"api/v1/enrollment/import/runs/{body.ImportRunId}");

        await kit.Scheduler.RunPendingAsync();
        var read = Assert.IsType<OkObjectResult>(await controller.GetImportRun(body.ImportRunId, CancellationToken.None));
        ((EnrollmentImportRunStatusResponse)read.Value!).Status.Should().Be(EnrollmentImportRunStatus.Completed);
        Assert.IsType<NotFoundResult>(await kit.Controller(tenantId: "t2").GetImportRun(body.ImportRunId, CancellationToken.None));
    }

    [Fact]
    public async Task TheRawFile_IsKeptUnderItsTenant()
    {
        var kit = new RunTestKit(new RecordingImport());
        await kit.Controller().ImportRaw834(Upload(File834(1)));
        var run = kit.Runs.All.Single();

        run.RawStore.Should().Be("InMemory");
        run.RawDocumentKey.Should().StartWith("tenants/t1/edi/inbound/834/");
        var act = () => kit.RawFiles.OpenAsync("t2", run.RawDocumentKey!);
        await act.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [Fact]
    public async Task TheGapReport_ReadsTheFileAsAStream()
    {
        var plans = new Moq.Mock<EnrollmentImportService.Clients.IBenefitPlanServiceClient>();
        var service = new PlanCodeGapReportService(plans.Object);
        var content = Isa + "ST*834*0001~INS*Y*18*021*28*A~REF*0F*SUB1~REF*1L*GRP1~NM1*IL*1*A~HD*021**HLT*PPO1*EMP~" +
                      "INS*Y*18*021*28*A~REF*0F*SUB2~REF*1L*GRP1~NM1*IL*1*B~HD*021**HLT*PPO1*EMP~HD*021**DEN*DEN1*EMP~SE*1*0001~";

        var report = await service.BuildReportAsync(new Streaming834Parser().ParseAsync(new MemoryStream(Encoding.UTF8.GetBytes(content)), "f.834"), "f.834", "t1");

        report.Unmapped.Select(e => e.ExternalPlanCode).Should().BeEquivalentTo(["PPO1", "DEN1"]);
    }
}
