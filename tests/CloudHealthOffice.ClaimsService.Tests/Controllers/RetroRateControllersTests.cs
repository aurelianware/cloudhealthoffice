using System.Net;
using System.Text;
using System.Text.Json;
using ClaimsService.Controllers;
using ClaimsService.Services;
using ClaimsService.Services.RetroRate;
using CloudHealthOffice.FeeScheduleEngine.Models;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace CloudHealthOffice.ClaimsService.Tests.Controllers;

/// <summary>
/// The retro rate endpoints: impact runs (a dry run) and reprocessing runs
/// (the propose, approve, execute steps that pay or recoup). Every call acts on
/// the caller's tenant and records the caller; service refusals map to
/// 404/400/409 rather than a 500; and the fee schedule change lookup treats an
/// unreachable or malformed benefit-plan-service as "no change".
/// </summary>
public class RetroRateControllersTests
{
    private const string Tenant = "tenant-1";
    private const string User = "examiner-7";

    private readonly IRetroRateReprocessingService _reprocessing = Substitute.For<IRetroRateReprocessingService>();
    private readonly IRetroRateReprocessingRepository _reprocessingStore = Substitute.For<IRetroRateReprocessingRepository>();
    private readonly IRetroRateImpactService _impact = Substitute.For<IRetroRateImpactService>();
    private readonly IRetroRateImpactRepository _impactStore = Substitute.For<IRetroRateImpactRepository>();
    private readonly ICurrentActor _actor = Substitute.For<ICurrentActor>();

    public RetroRateControllersTests()
    {
        _actor.UserId.Returns(User);
        _actor.TenantId.Returns("token-tenant");
    }

    private static ControllerContext Context(string? correlationId = null)
    {
        var http = new DefaultHttpContext();
        http.Items["TenantId"] = Tenant;
        if (correlationId is not null) http.Request.Headers["X-Correlation-Id"] = correlationId;
        return new ControllerContext { HttpContext = http };
    }

    private RetroRateReprocessingController Reprocessing(string? correlationId = null) =>
        new(_reprocessing, _reprocessingStore, _actor) { ControllerContext = Context(correlationId) };

    private RetroRateImpactController Impact() =>
        new(_impact, _impactStore, _actor) { ControllerContext = Context() };

    private static RetroRateReprocessingRun ReprocessingRun(string status = RetroRateReprocessingStatus.Proposed) =>
        new() { Id = "run-1", TenantId = Tenant, Status = status };

    // ── Reprocessing runs ──────────────────────────────────────────────

    [Fact]
    public async Task Propose_plans_the_run_for_the_callers_tenant_and_records_the_caller()
    {
        var request = new RetroRateReprocessingRunRequest { ImpactRunId = "impact-1" };
        _reprocessing.ProposeAsync(Tenant, request, User, Arg.Any<CancellationToken>()).Returns(ReprocessingRun());

        var result = await Reprocessing().Propose(request, default);

        Assert.Equal("run-1", Assert.IsType<RetroRateReprocessingRun>(Assert.IsType<OkObjectResult>(result.Result).Value).Id);
    }

    [Fact]
    public async Task Propose_without_a_body_still_reaches_the_service_which_decides_it_is_invalid()
    {
        _reprocessing.ProposeAsync(Tenant, Arg.Any<RetroRateReprocessingRunRequest>(), User, Arg.Any<CancellationToken>())
            .Throws(new RetroRateReprocessingException(RetroRateReprocessingException.Kind.Invalid, "impactRunId is required."));

        var result = await Reprocessing().Propose(null, default);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task Approve_records_the_approver()
    {
        _reprocessing.ApproveAsync(Tenant, "run-1", User, Arg.Any<CancellationToken>())
            .Returns(ReprocessingRun(RetroRateReprocessingStatus.Approved));

        var result = await Reprocessing().Approve("run-1", default);

        Assert.IsType<OkObjectResult>(result.Result);
        await _reprocessing.Received(1).ApproveAsync(Tenant, "run-1", User, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(RetroRateReprocessingException.Kind.NotFound, typeof(NotFoundObjectResult))]
    [InlineData(RetroRateReprocessingException.Kind.Invalid, typeof(BadRequestObjectResult))]
    [InlineData(RetroRateReprocessingException.Kind.Conflict, typeof(ConflictObjectResult))]
    public async Task Approval_refusals_map_to_status_codes(RetroRateReprocessingException.Kind kind, Type expected)
    {
        // e.g. the proposer approving their own run is a Conflict, not a 500.
        _reprocessing.ApproveAsync(Tenant, "run-1", User, Arg.Any<CancellationToken>())
            .Throws(new RetroRateReprocessingException(kind, "refused"));

        var result = await Reprocessing().Approve("run-1", default);

        Assert.IsType(expected, result.Result);
    }

    [Fact]
    public async Task Execute_passes_the_callers_correlation_id()
    {
        _reprocessing.ExecuteAsync(Tenant, "run-1", User, "corr-123", Arg.Any<CancellationToken>())
            .Returns(ReprocessingRun(RetroRateReprocessingStatus.Completed));

        var result = await Reprocessing("corr-123").Execute("run-1", default);

        Assert.IsType<OkObjectResult>(result.Result);
        await _reprocessing.Received(1).ExecuteAsync(Tenant, "run-1", User, "corr-123", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Executing_an_unapproved_run_is_409()
    {
        _reprocessing.ExecuteAsync(Tenant, "run-1", User, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Throws(new RetroRateReprocessingException(RetroRateReprocessingException.Kind.Conflict, "Run is not approved."));

        var result = await Reprocessing().Execute("run-1", default);

        Assert.IsType<ConflictObjectResult>(result.Result);
    }

    [Fact]
    public async Task Cancel_records_who_cancelled()
    {
        _reprocessing.CancelAsync(Tenant, "run-1", User, Arg.Any<CancellationToken>())
            .Returns(ReprocessingRun(RetroRateReprocessingStatus.Cancelled));

        Assert.IsType<OkObjectResult>((await Reprocessing().Cancel("run-1", default)).Result);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(50, 50)]
    [InlineData(100_000, 500)]
    public async Task Listing_reprocessing_runs_clamps_the_limit(int requested, int sent)
    {
        _reprocessingStore.ListAsync(Tenant, sent, Arg.Any<CancellationToken>()).Returns([ReprocessingRun()]);

        var result = await Reprocessing().List(requested);

        Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<RetroRateReprocessingRun>>(Assert.IsType<OkObjectResult>(result.Result).Value));
    }

    [Fact]
    public async Task Another_tenants_run_is_404()
    {
        _reprocessingStore.GetAsync(Tenant, "run-other", Arg.Any<CancellationToken>()).Returns((RetroRateReprocessingRun?)null);
        _reprocessingStore.GetAsync(Tenant, "run-1", Arg.Any<CancellationToken>()).Returns(ReprocessingRun());

        Assert.IsType<NotFoundResult>((await Reprocessing().Get("run-other", default)).Result);
        Assert.IsType<OkObjectResult>((await Reprocessing().Get("run-1", default)).Result);
    }

    [Fact]
    public async Task Without_a_tenant_in_the_request_the_tokens_tenant_is_used()
    {
        var controller = new RetroRateReprocessingController(_reprocessing, _reprocessingStore, _actor)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
        _reprocessingStore.GetAsync("token-tenant", "run-1", Arg.Any<CancellationToken>()).Returns(ReprocessingRun());

        Assert.IsType<OkObjectResult>((await controller.Get("run-1", default)).Result);
    }

    // ── Impact runs ────────────────────────────────────────────────────

    [Theory]
    [InlineData(null, null, null)]
    [InlineData("", null, null)]
    [InlineData("fs-1", 0, null)]
    [InlineData("fs-1", null, 0L)]
    public async Task An_invalid_impact_request_is_400_and_nothing_runs(string? scheduleId, int? limit, long? revision)
    {
        var request = scheduleId is null && limit is null && revision is null
            ? null
            : new RetroRateImpactRunRequest { FeeScheduleId = scheduleId ?? string.Empty, Limit = limit, Revision = revision };

        var result = await Impact().Run(request, default);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        await _impact.DidNotReceiveWithAnyArgs().RunAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task An_impact_run_measures_the_change_for_the_callers_tenant()
    {
        var request = new RetroRateImpactRunRequest { FeeScheduleId = "fs-1", Revision = 3 };
        _impact.RunAsync(Tenant, request, User, Arg.Any<CancellationToken>()).Returns(new RetroRateImpactRun { Id = "impact-1" });

        var result = await Impact().Run(request, default);

        Assert.Equal("impact-1", Assert.IsType<RetroRateImpactRun>(Assert.IsType<OkObjectResult>(result.Result).Value).Id);
    }

    [Fact]
    public async Task An_unknown_fee_schedule_change_is_404()
    {
        _impact.RunAsync(Tenant, Arg.Any<RetroRateImpactRunRequest>(), User, Arg.Any<CancellationToken>())
            .Throws(new FeeScheduleChangeNotFoundException("No change for fs-1."));

        var result = await Impact().Run(new RetroRateImpactRunRequest { FeeScheduleId = "fs-1" }, default);

        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    [Fact]
    public async Task Impact_results_are_404_for_a_run_the_tenant_does_not_have()
    {
        _impactStore.GetRunAsync(Tenant, "missing", Arg.Any<CancellationToken>()).Returns((RetroRateImpactRun?)null);

        Assert.IsType<NotFoundResult>((await Impact().GetRun("missing", default)).Result);
        Assert.IsType<NotFoundResult>((await Impact().ListResults("missing")).Result);
        Assert.IsType<NotFoundResult>(await Impact().ClaimsCsv("missing", default));
        await _impactStore.DidNotReceiveWithAnyArgs().ListResultsAsync(default!, default!, default, default, default);
    }

    [Fact]
    public async Task An_unknown_direction_filter_is_400()
    {
        var result = await Impact().ListResults("impact-1", direction: "Sideways");

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task Impact_results_filter_by_direction_and_clamp_the_limit()
    {
        _impactStore.GetRunAsync(Tenant, "impact-1", Arg.Any<CancellationToken>()).Returns(new RetroRateImpactRun { Id = "impact-1" });
        _impactStore.ListResultsAsync(Tenant, "impact-1", RetroRateDirection.Underpaid, 1000, Arg.Any<CancellationToken>())
            .Returns([new RetroRateImpactResult { ClaimId = "C1", Direction = RetroRateDirection.Underpaid }]);

        var result = await Impact().ListResults("impact-1", RetroRateDirection.Underpaid, limit: 1_000_000);

        Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<RetroRateImpactResult>>(Assert.IsType<OkObjectResult>(result.Result).Value));
    }

    [Fact]
    public async Task Listing_impact_runs_clamps_the_limit()
    {
        _impactStore.ListRunsAsync(Tenant, 1, Arg.Any<CancellationToken>()).Returns([]);

        Assert.IsType<OkObjectResult>((await Impact().ListRuns(-3)).Result);
        await _impactStore.Received(1).ListRunsAsync(Tenant, 1, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_claims_csv_is_a_file_named_for_the_run()
    {
        _impactStore.GetRunAsync(Tenant, "impact-1", Arg.Any<CancellationToken>()).Returns(new RetroRateImpactRun { Id = "impact-1" });
        _impactStore.ListResultsAsync(Tenant, "impact-1", null, RetroRateImpactController.MaxCsvResults, Arg.Any<CancellationToken>())
            .Returns([new RetroRateImpactResult { ClaimNumber = "=HYPERLINK(1)", ClaimId = "C1", NetPaidDelta = 12.5m }]);

        var file = Assert.IsType<FileContentResult>(await Impact().ClaimsCsv("impact-1", default));

        Assert.Equal("retro-rate-impact-impact-1.csv", file.FileDownloadName);
        var csv = Encoding.UTF8.GetString(file.FileContents);
        Assert.StartsWith("claim_number,", csv);
        Assert.Contains("'=HYPERLINK(1)", csv); // a spreadsheet must not run it as a formula
        Assert.Contains("12.50", csv);
    }

    // ── Fee schedule change lookup ─────────────────────────────────────

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public HttpRequestMessage? Last { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Last = request;
            return Task.FromResult(respond(request));
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public string? Name { get; private set; }

        public HttpClient CreateClient(string name)
        {
            Name = name;
            return new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri("http://benefit-plan.test") };
        }
    }

    private static HttpResponseMessage Changes(params long[] revisions) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(revisions.Select(r => new
        {
            id = $"chg-{r}", feeScheduleId = "fs 1", newRevision = r, kind = "Replaced",
        })), Encoding.UTF8, "application/json"),
    };

    private static (HttpFeeScheduleChangeClient Client, StubHandler Handler, Factory Factory) ChangeClient(
        Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new StubHandler(respond);
        var factory = new Factory(handler);
        return (new HttpFeeScheduleChangeClient(factory, NullLogger<HttpFeeScheduleChangeClient>.Instance), handler, factory);
    }

    [Fact]
    public async Task The_latest_change_is_read_from_benefit_plan_service_for_the_tenant()
    {
        var (client, handler, factory) = ChangeClient(_ => Changes(1, 3, 2));

        var change = await client.GetChangeAsync(Tenant, "fs 1", revision: null);

        Assert.Equal(3, change!.NewRevision);
        Assert.Equal(UpstreamClientNames.BenefitPlanService, factory.Name);
        Assert.Equal(Tenant, Assert.Single(handler.Last!.Headers.GetValues("X-Tenant-ID")));
        Assert.Contains("scheduleId=fs%201", handler.Last.RequestUri!.Query);
    }

    [Fact]
    public async Task A_named_revision_returns_that_change_or_null()
    {
        var (client, _, _) = ChangeClient(_ => Changes(1, 2, 3));

        Assert.Equal(2, (await client.GetChangeAsync(Tenant, "fs 1", revision: 2))!.NewRevision);
        Assert.Null(await client.GetChangeAsync(Tenant, "fs 1", revision: 9));
    }

    [Fact]
    public async Task A_failed_unreachable_or_malformed_response_reads_as_no_change()
    {
        foreach (var respond in new Func<HttpRequestMessage, HttpResponseMessage>[]
        {
            _ => new HttpResponseMessage(HttpStatusCode.InternalServerError),
            _ => throw new HttpRequestException("connection refused"),
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{not json", Encoding.UTF8, "application/json") },
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("null", Encoding.UTF8, "application/json") },
        })
        {
            var (client, _, _) = ChangeClient(respond);
            Assert.Null(await client.GetChangeAsync(Tenant, "fs 1", revision: null));
        }
    }

    [Fact]
    public async Task A_cancelled_caller_is_not_swallowed()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var (client, _, _) = ChangeClient(_ => throw new OperationCanceledException(cts.Token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetChangeAsync(Tenant, "fs 1", null, cts.Token));
    }
}
