using CloudHealthOffice.Infrastructure.Security;
using EncounterSubmissionService.Controllers;
using EncounterSubmissionService.Models;
using EncounterSubmissionService.StateRoutes;
using Microsoft.AspNetCore.Mvc;
using static CloudHealthOffice.EncounterSubmissionService.Tests.StateRoutes.StateRouteTestData;

namespace CloudHealthOffice.EncounterSubmissionService.Tests.Controllers;

/// <summary>
/// The Texas / California console API: every call runs under the caller's own
/// tenant, readiness explains what is missing, and rework / response outcomes
/// map to the right HTTP status.
/// </summary>
public class StateEncounterRoutesControllerTests
{
    private const string Tenant = "tenant-1";

    private readonly Mock<IStateEncounterBatchService> _batches = new(MockBehavior.Strict);
    private readonly Mock<IStateResponseReconciler> _responses = new(MockBehavior.Strict);
    private readonly Mock<IEncounterReworkService> _rework = new(MockBehavior.Strict);
    private readonly Mock<ICurrentActor> _actor = new();

    public StateEncounterRoutesControllerTests()
    {
        _actor.SetupGet(a => a.TenantId).Returns(Tenant);
        _actor.SetupGet(a => a.UserId).Returns("analyst-7");
    }

    private StateEncounterRoutesController Controller(IStateEncounterRouteRegistry? registry = null) =>
        new(registry ?? Registry(tx: TexasOptions()), _batches.Object, _responses.Object, _rework.Object, _actor.Object);

    private static T Body<T>(IActionResult result) => (T)((ObjectResult)result).Value!;

    // ── Routes ─────────────────────────────────────────────────────────

    [Fact]
    public void Routes_ListEveryState_WithReadinessForTheCallersTenant()
    {
        var txOptions = TexasOptions();
        txOptions.UsageIndicator = "P";
        txOptions.DeadlineRule = EncounterDeadlineRule.DaysAfterAdjudication;
        txOptions.DeadlineDays = 45;

        var result = Controller(Registry(tx: txOptions)).GetRoutes();

        var routes = ((IEnumerable<StateRouteStatus>)((OkObjectResult)result.Result!).Value!).ToDictionary(r => r.StateCode);
        routes.Keys.Should().BeEquivalentTo("FL", "TX", "CA");

        routes["FL"].Ready.Should().BeTrue();
        routes["FL"].Format.Should().Be("FmmisJson");
        routes["FL"].UsageIndicator.Should().BeNull("Florida is not an X12 route");
        routes["FL"].DeadlineDays.Should().Be(60);

        routes["TX"].Ready.Should().BeTrue();
        routes["TX"].Receiver.Should().Be("TMHP");
        routes["TX"].UsageIndicator.Should().Be("P");
        routes["TX"].DeadlineRule.Should().Be(nameof(EncounterDeadlineRule.DaysAfterAdjudication));
        routes["TX"].DeadlineDays.Should().Be(45);
        routes["TX"].ResponseFiles.Should().Equal("999", "277CA");

        routes["CA"].Ready.Should().BeFalse();
        routes["CA"].NotReadyReason.Should().Contain("not enabled");
        routes["CA"].UsageIndicator.Should().Be("T", "an unconfigured X12 route defaults to test");
        routes["CA"].DeadlineRule.Should().Be(nameof(EncounterDeadlineRule.DaysAfterEndOfAdjudicationMonth));
        routes["CA"].DeadlineDays.Should().Be(30);
    }

    [Fact]
    public void Routes_ForATenantNotEnrolled_SayWhy()
    {
        _actor.SetupGet(a => a.TenantId).Returns("tenant-2");

        var result = Controller().GetRoutes();

        var tx = ((IEnumerable<StateRouteStatus>)((OkObjectResult)result.Result!).Value!).Single(r => r.StateCode == "TX");
        tx.Ready.Should().BeFalse();
        tx.NotReadyReason.Should().Be("tenant is not enrolled on the TX route");
    }

    [Fact]
    public void Routes_WithMissingTenantIdentities_NameTheMissingFields()
    {
        var tx = TexasOptions();
        tx.Tenants[Tenant].PlanPayerId = null;

        var result = Controller(Registry(tx: tx)).GetRoutes();

        var status = ((IEnumerable<StateRouteStatus>)((OkObjectResult)result.Result!).Value!).Single(r => r.StateCode == "TX");
        status.Ready.Should().BeFalse();
        status.NotReadyReason.Should().Contain("PlanPayerId");
    }

    // ── Files ──────────────────────────────────────────────────────────

    [Fact]
    public async Task ListFiles_IsScopedToTheCallersTenant()
    {
        var files = new List<StateEncounterFile> { new() { TenantId = Tenant, FileName = "a.x12" } };
        _batches.Setup(b => b.ListFilesAsync(Tenant, "TX", 10)).ReturnsAsync(files);

        var result = await Controller().ListFiles("TX", 10);

        ((OkObjectResult)result.Result!).Value.Should().BeSameAs(files);
    }

    [Fact]
    public async Task DownloadFile_ReturnsTheContentAsWritten()
    {
        _batches.Setup(b => b.GetFileAsync(Tenant, "file-1")).ReturnsAsync(new StateEncounterFile
        {
            TenantId = Tenant, FileName = "TMHP_4A_837P.x12", Content = "ISA*00*~IEA*1*000000042~"
        });

        var result = await Controller().DownloadFile("file-1");

        var file = result.Should().BeOfType<FileContentResult>().Subject;
        file.ContentType.Should().Be("text/plain");
        file.FileDownloadName.Should().Be("TMHP_4A_837P.x12");
        System.Text.Encoding.ASCII.GetString(file.FileContents).Should().Be("ISA*00*~IEA*1*000000042~");
    }

    [Fact]
    public async Task DownloadFile_NotFoundForThisTenant_Is404()
    {
        _batches.Setup(b => b.GetFileAsync(Tenant, "someone-elses")).ReturnsAsync((StateEncounterFile?)null);

        (await Controller().DownloadFile("someone-elses")).Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task MarkTransmitted_ReturnsTheFile_Or404()
    {
        var file = new StateEncounterFile { Id = "file-1", TenantId = Tenant, TransmittedAt = DateTime.UtcNow };
        _batches.Setup(b => b.MarkTransmittedAsync(Tenant, "file-1", It.IsAny<CancellationToken>())).ReturnsAsync(file);
        _batches.Setup(b => b.MarkTransmittedAsync(Tenant, "missing", It.IsAny<CancellationToken>()))
            .ReturnsAsync((StateEncounterFile?)null);

        ((OkObjectResult)(await Controller().MarkTransmitted("file-1", default)).Result!).Value.Should().BeSameAs(file);
        (await Controller().MarkTransmitted("missing", default)).Result.Should().BeOfType<NotFoundResult>();
    }

    // ── Responses ──────────────────────────────────────────────────────

    [Fact]
    public async Task ApplyResponse_HandsTheFileToTheReconciler_ForTheCallersTenant()
    {
        var applied = new StateResponseResult { Kind = "999", Matched = 3, Accepted = 3 };
        _responses.Setup(r => r.ApplyAsync(StateEncounterRoute.Texas, Tenant, "resp.999", "ISA*~", It.IsAny<CancellationToken>()))
            .ReturnsAsync(applied);

        var result = await Controller().ApplyResponse("tx", new StateResponseRequest { FileName = "resp.999", Content = "ISA*~" }, default);

        ((OkObjectResult)result.Result!).Value.Should().BeSameAs(applied);
    }

    [Theory]
    [InlineData("FL")]
    [InlineData("NY")]
    public async Task ApplyResponse_ForAStateWithoutAnX12Route_Is400(string state)
    {
        var result = await Controller().ApplyResponse(state, new StateResponseRequest { Content = "ISA*~" }, default);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ApplyResponse_WithoutContent_Is400(string content)
    {
        var result = await Controller().ApplyResponse("TX", new StateResponseRequest { Content = content }, default);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task ApplyResponse_UnsupportedFormat_Is422()
    {
        _responses.Setup(r => r.ApplyAsync(It.IsAny<StateEncounterRoute>(), Tenant, It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new NotSupportedException("EVR XML is not supported yet"));

        var result = await Controller().ApplyResponse("CA", new StateResponseRequest { Content = "<EVR/>" }, default);

        result.Result.Should().BeOfType<UnprocessableEntityObjectResult>();
    }

    [Fact]
    public async Task ApplyResponse_UnreadableFile_Is400()
    {
        _responses.Setup(r => r.ApplyAsync(It.IsAny<StateEncounterRoute>(), Tenant, It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new FormatException("not an X12 interchange"));

        var result = await Controller().ApplyResponse("TX", new StateResponseRequest { Content = "garbage" }, default);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    // ── Rework ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ListRework_NormalizesTheReasonFilter()
    {
        var page = new EncounterReworkPage { Page = 2, PageSize = 25 };
        _rework.Setup(r => r.ListAsync(Tenant, "CA", "STATE_REJECTED", 2, 25, It.IsAny<CancellationToken>())).ReturnsAsync(page);

        var result = await Controller().ListRework("CA", " state_rejected ", 2, 25);

        ((OkObjectResult)result.Result!).Value.Should().BeSameAs(page);
    }

    [Fact]
    public async Task ListRework_WithoutAReason_PassesNull()
    {
        var page = new EncounterReworkPage();
        _rework.Setup(r => r.ListAsync(Tenant, null, null, 1, 50, It.IsAny<CancellationToken>())).ReturnsAsync(page);

        var result = await Controller().ListRework();

        ((OkObjectResult)result.Result!).Value.Should().BeSameAs(page);
    }

    [Fact]
    public async Task Requeue_RecordsWhoDidIt_AndTheirNote()
    {
        var submission = new EncounterSubmission { Id = "sub-1", TenantId = Tenant, Status = EncounterSubmissionStatus.Pending };
        _rework.Setup(r => r.RequeueAsync(Tenant, "sub-1", "analyst-7", "fixed member address", It.IsAny<CancellationToken>()))
            .ReturnsAsync((ReworkOutcome.Done, submission));

        var result = await Controller().Requeue("sub-1", new ReworkActionRequest { Note = "fixed member address" }, default);

        Body<EncounterSubmission>(result).Should().BeSameAs(submission);
        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task Requeue_WithoutABody_PassesNoNote()
    {
        _rework.Setup(r => r.RequeueAsync(Tenant, "sub-1", "analyst-7", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ReworkOutcome.NotFound, (EncounterSubmission?)null));

        (await Controller().Requeue("sub-1", null, default)).Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Requeue_OfAnEncounterNotInRework_Is409_WithItsStatus()
    {
        _rework.Setup(r => r.RequeueAsync(Tenant, "sub-1", "analyst-7", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ReworkOutcome.NotInRework, new EncounterSubmission { Status = EncounterSubmissionStatus.Accepted }));

        var result = await Controller().Requeue("sub-1", new ReworkActionRequest(), default);

        var conflict = result.Should().BeOfType<ConflictObjectResult>().Subject;
        conflict.Value!.ToString().Should().Contain("Accepted");
    }

    [Fact]
    public async Task Close_WithoutAReason_Is400()
    {
        _rework.Setup(r => r.CloseAsync(Tenant, "sub-1", "analyst-7", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ReworkOutcome.ReasonRequired, (EncounterSubmission?)null));

        (await Controller().Close("sub-1", null, default)).Should().BeOfType<BadRequestObjectResult>();
    }

    [Theory]
    [InlineData(ReworkOutcome.Done, typeof(OkObjectResult))]
    [InlineData(ReworkOutcome.NotFound, typeof(NotFoundResult))]
    [InlineData(ReworkOutcome.NotInRework, typeof(ConflictObjectResult))]
    public async Task Close_MapsEachOutcome(ReworkOutcome outcome, Type expected)
    {
        _rework.Setup(r => r.CloseAsync(Tenant, "sub-1", "analyst-7", "duplicate", It.IsAny<CancellationToken>()))
            .ReturnsAsync((outcome, new EncounterSubmission { Id = "sub-1", Status = EncounterSubmissionStatus.ClosedNotReportable }));

        var result = await Controller().Close("sub-1", new ReworkActionRequest { Note = "duplicate" }, default);

        result.Should().BeOfType(expected);
    }
}
