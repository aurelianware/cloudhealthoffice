using System.Net;
using System.Text;
using System.Text.Json;
using MemberService.Controllers;
using MemberService.Services;
using Microsoft.Extensions.Options;

namespace MemberService.Tests.Services;

/// <summary>
/// The HTTP clients member-service uses to reach coverage-service,
/// enrollment-import-service and accumulator-service. Every request carries
/// the caller's tenant and an escaped member id; an unconfigured host or a
/// failed downstream call surfaces as <see cref="DownstreamUnavailableException"/>
/// (the controller's 503), and a PCP validation 400 comes back as an outcome,
/// not an exception.
/// </summary>
public class HttpDownstreamClientsTests
{
    private const string Tenant = "tenant-a";
    private const string Member = "M 1/2"; // needs escaping

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();
        public List<string?> Bodies { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            Bodies.Add(request.Content is null ? null : await request.Content.ReadAsStringAsync(ct));
            return respond(request);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, object? body) => new(status)
    {
        Content = new StringContent(body is string s ? s : JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
    };

    private static (HttpClient Http, StubHandler Handler) Http(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new StubHandler(respond);
        return (new HttpClient(handler) { BaseAddress = new Uri("http://downstream.test") }, handler);
    }

    private static IOptions<DownstreamOptions> Configured() => Options.Create(new DownstreamOptions
    {
        CoverageService = new DownstreamService { BaseUrl = "http://downstream.test" },
        EnrollmentImportService = new DownstreamService { BaseUrl = "http://downstream.test" },
        AccumulatorService = new DownstreamService { BaseUrl = "http://downstream.test" },
    });

    private static IOptions<DownstreamOptions> Unconfigured() => Options.Create(new DownstreamOptions());

    private static void ShouldBeTenantScoped(HttpRequestMessage request)
    {
        request.Headers.GetValues("X-Tenant-ID").Should().ContainSingle().Which.Should().Be(Tenant);
        request.RequestUri!.AbsoluteUri.Should().Contain("M%201%2F2");
    }

    // ── Unconfigured hosts ─────────────────────────────────────────────

    [Fact]
    public async Task Every_client_refuses_without_a_base_url_and_sends_nothing()
    {
        var (http, handler) = Http(_ => throw new InvalidOperationException("must not be called"));
        var coverage = new HttpCoverageServiceClient(http, Unconfigured());
        var enrollment = new HttpEnrollmentImportServiceClient(http, Unconfigured());
        var accumulators = new HttpAccumulatorServiceClient(http, Unconfigured());

        var calls = new Func<Task>[]
        {
            () => coverage.GetPcpAsync(Tenant, Member),
            () => coverage.AssignPcpAsync(Tenant, Member, new AssignPcpRequest()),
            () => coverage.GetPcpHistoryAsync(Tenant, Member),
            () => coverage.GetCoverageHistoryAsync(Tenant, Member),
            () => coverage.TerminateCoverageAsync(Tenant, Member, new TerminateMemberRequest()),
            () => enrollment.Get834TransactionsAsync(Tenant, Member),
            () => enrollment.GetEnrollmentEventsAsync(Tenant, Member),
            () => accumulators.GetAccumulatorsAsync(Tenant, Member),
        };

        foreach (var call in calls)
            (await call.Should().ThrowAsync<DownstreamUnavailableException>()).Which.Detail.Should().Contain("BaseUrl");
        handler.Requests.Should().BeEmpty();
    }

    // ── coverage-service ───────────────────────────────────────────────

    [Fact]
    public async Task Get_pcp_reads_the_members_pcp_for_the_callers_tenant()
    {
        var (http, handler) = Http(_ => Json(HttpStatusCode.OK, new MemberPcpResponse { ProviderId = "P1", NPI = "1234567893" }));

        var pcp = await new HttpCoverageServiceClient(http, Configured()).GetPcpAsync(Tenant, Member);

        pcp.ProviderId.Should().Be("P1");
        handler.Requests.Single().Method.Should().Be(HttpMethod.Get);
        handler.Requests.Single().RequestUri!.AbsolutePath.Should().EndWith("/pcp");
        ShouldBeTenantScoped(handler.Requests.Single());
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task A_failed_pcp_read_is_downstream_unavailable(HttpStatusCode status)
    {
        var (http, _) = Http(_ => new HttpResponseMessage(status));

        var act = () => new HttpCoverageServiceClient(http, Configured()).GetPcpAsync(Tenant, Member);

        (await act.Should().ThrowAsync<DownstreamUnavailableException>()).Which.ServiceName.Should().Be("coverage-service");
    }

    [Fact]
    public async Task An_empty_pcp_body_is_downstream_unavailable()
    {
        var (http, _) = Http(_ => Json(HttpStatusCode.OK, "null"));

        var act = () => new HttpCoverageServiceClient(http, Configured()).GetPcpAsync(Tenant, Member);

        await act.Should().ThrowAsync<DownstreamUnavailableException>();
    }

    [Fact]
    public async Task Assigning_a_pcp_puts_the_request_and_returns_the_new_pcp()
    {
        var (http, handler) = Http(_ => Json(HttpStatusCode.OK, new MemberPcpResponse { ProviderId = "P2" }));

        var outcome = await new HttpCoverageServiceClient(http, Configured())
            .AssignPcpAsync(Tenant, Member, new AssignPcpRequest { ProviderId = "P2", ProviderNpi = "1234567893" });

        outcome.IsSuccess.Should().BeTrue();
        outcome.Pcp!.ProviderId.Should().Be("P2");
        handler.Requests.Single().Method.Should().Be(HttpMethod.Put);
        handler.Bodies.Single().Should().Contain("1234567893");
        ShouldBeTenantScoped(handler.Requests.Single());
    }

    [Fact]
    public async Task A_pcp_validation_400_is_an_outcome_carrying_coverage_services_problem()
    {
        var (http, _) = Http(_ => Json(HttpStatusCode.BadRequest,
            new PcpValidationProblem { Code = "PCP_OUT_OF_NETWORK", Field = "providerId", Message = "Not in network" }));

        var outcome = await new HttpCoverageServiceClient(http, Configured()).AssignPcpAsync(Tenant, Member, new AssignPcpRequest());

        outcome.IsSuccess.Should().BeFalse();
        outcome.ValidationError!.Code.Should().Be("PCP_OUT_OF_NETWORK");
        outcome.ValidationError.Field.Should().Be("providerId");
    }

    [Theory]
    [InlineData("not json", "application/json")]
    [InlineData("<html>bad</html>", "text/html")]
    public async Task A_pcp_400_without_a_readable_problem_still_degrades_to_a_validation_outcome(string body, string mediaType)
    {
        var (http, _) = Http(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(body, Encoding.UTF8, mediaType),
        });

        var outcome = await new HttpCoverageServiceClient(http, Configured()).AssignPcpAsync(Tenant, Member, new AssignPcpRequest());

        outcome.IsSuccess.Should().BeFalse();
        outcome.ValidationError!.Code.Should().Be("VALIDATION_FAILED");
    }

    [Fact]
    public async Task A_pcp_assignment_success_body_of_the_wrong_shape_is_downstream_unavailable()
    {
        var (http, _) = Http(_ => Json(HttpStatusCode.OK, "[1,2,3]"));

        var act = () => new HttpCoverageServiceClient(http, Configured()).AssignPcpAsync(Tenant, Member, new AssignPcpRequest());

        await act.Should().ThrowAsync<DownstreamUnavailableException>();
    }

    [Fact]
    public async Task A_pcp_assignment_server_error_is_downstream_unavailable()
    {
        var (http, _) = Http(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var act = () => new HttpCoverageServiceClient(http, Configured()).AssignPcpAsync(Tenant, Member, new AssignPcpRequest());

        await act.Should().ThrowAsync<DownstreamUnavailableException>();
    }

    [Fact]
    public async Task Pcp_history_is_read_and_a_null_body_is_an_empty_list()
    {
        var items = new[] { new PcpAssignmentHistoryItem { Id = "h1", MemberId = Member } };
        var (http, handler) = Http(_ => Json(HttpStatusCode.OK, items));
        var client = new HttpCoverageServiceClient(http, Configured());

        (await client.GetPcpHistoryAsync(Tenant, Member)).Should().ContainSingle(i => i.Id == "h1");
        handler.Requests.Single().RequestUri!.AbsolutePath.Should().EndWith("/pcp/history");
        ShouldBeTenantScoped(handler.Requests.Single());

        var (nullHttp, _) = Http(_ => Json(HttpStatusCode.OK, "null"));
        (await new HttpCoverageServiceClient(nullHttp, Configured()).GetPcpHistoryAsync(Tenant, Member)).Should().BeEmpty();
    }

    [Fact]
    public async Task Pcp_history_of_the_wrong_shape_is_downstream_unavailable_not_a_500()
    {
        var (http, _) = Http(_ => Json(HttpStatusCode.OK, "{\"unexpected\":true}"));

        var act = () => new HttpCoverageServiceClient(http, Configured()).GetPcpHistoryAsync(Tenant, Member);

        (await act.Should().ThrowAsync<DownstreamUnavailableException>()).Which.Detail.Should().Contain("deserialization");
    }

    [Fact]
    public async Task Coverage_history_is_read_for_the_callers_tenant()
    {
        var (http, handler) = Http(_ => Json(HttpStatusCode.OK, new[] { new CoverageHistoryEvent { EventType = "Enrolled" } }));

        var history = await new HttpCoverageServiceClient(http, Configured()).GetCoverageHistoryAsync(Tenant, Member);

        history.Should().ContainSingle(e => e.EventType == "Enrolled");
        handler.Requests.Single().RequestUri!.AbsolutePath.Should().EndWith("/history");
        ShouldBeTenantScoped(handler.Requests.Single());
    }

    [Fact]
    public async Task A_failed_coverage_history_read_is_downstream_unavailable()
    {
        var (http, _) = Http(_ => new HttpResponseMessage(HttpStatusCode.BadGateway));

        var act = () => new HttpCoverageServiceClient(http, Configured()).GetCoverageHistoryAsync(Tenant, Member);

        await act.Should().ThrowAsync<DownstreamUnavailableException>();
    }

    [Fact]
    public async Task Terminating_coverage_posts_the_request()
    {
        var (http, handler) = Http(_ => new HttpResponseMessage(HttpStatusCode.NoContent));

        await new HttpCoverageServiceClient(http, Configured()).TerminateCoverageAsync(Tenant, Member,
            new TerminateMemberRequest { CoverageId = "C1", ReasonCode = "07" });

        var request = handler.Requests.Single();
        request.Method.Should().Be(HttpMethod.Post);
        request.RequestUri!.AbsolutePath.Should().EndWith("/terminate");
        handler.Bodies.Single().Should().Contain("C1");
        ShouldBeTenantScoped(request);
    }

    [Fact]
    public async Task A_refused_termination_is_downstream_unavailable_so_the_member_is_not_reported_terminated()
    {
        var (http, _) = Http(_ => new HttpResponseMessage(HttpStatusCode.Conflict));

        var act = () => new HttpCoverageServiceClient(http, Configured())
            .TerminateCoverageAsync(Tenant, Member, new TerminateMemberRequest());

        await act.Should().ThrowAsync<DownstreamUnavailableException>();
    }

    // ── enrollment-import-service ──────────────────────────────────────

    [Fact]
    public async Task The_834_history_is_read_by_escaped_member_id_for_the_callers_tenant()
    {
        var (http, handler) = Http(_ => Json(HttpStatusCode.OK, new[] { new Enrollment834Record { TransactionId = "T1" } }));

        var records = await new HttpEnrollmentImportServiceClient(http, Configured()).Get834TransactionsAsync(Tenant, Member);

        records.Should().ContainSingle(r => r.TransactionId == "T1");
        handler.Requests.Single().RequestUri!.Query.Should().Be("?memberId=M%201%2F2");
        handler.Requests.Single().Headers.GetValues("X-Tenant-ID").Should().Equal(Tenant);
    }

    [Fact]
    public async Task A_null_834_body_is_an_empty_list_and_a_failure_is_downstream_unavailable()
    {
        var (nullHttp, _) = Http(_ => Json(HttpStatusCode.OK, "null"));
        (await new HttpEnrollmentImportServiceClient(nullHttp, Configured()).Get834TransactionsAsync(Tenant, Member)).Should().BeEmpty();

        var (failHttp, _) = Http(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var act = () => new HttpEnrollmentImportServiceClient(failHttp, Configured()).Get834TransactionsAsync(Tenant, Member);
        (await act.Should().ThrowAsync<DownstreamUnavailableException>()).Which.ServiceName.Should().Be("enrollment-import-service");
    }

    [Fact]
    public async Task Enrollment_events_pass_every_filter_escaped()
    {
        var (http, handler) = Http(_ => Json(HttpStatusCode.OK, new EnrollmentEventListResponse { ContinuationToken = "next" }));

        var page = await new HttpEnrollmentImportServiceClient(http, Configured()).GetEnrollmentEventsAsync(
            Tenant, Member, type: "Add Member", from: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            to: new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc), limit: 25, continuationToken: "a+b/c");

        page.ContinuationToken.Should().Be("next");
        var uri = handler.Requests.Single().RequestUri!;
        uri.AbsolutePath.Should().Be("/api/v1/members/M%201%2F2/enrollment-events");
        uri.Query.Should().Contain("limit=25")
            .And.Contain("type=Add%20Member")
            .And.Contain("from=2026-01-01T00%3A00%3A00.0000000Z")
            .And.Contain("to=2026-02-01T00%3A00%3A00.0000000Z")
            .And.Contain("continuationToken=a%2Bb%2Fc");
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(10_000, 200)]
    public async Task Enrollment_event_page_size_is_clamped(int requested, int sent)
    {
        var (http, handler) = Http(_ => Json(HttpStatusCode.OK, new EnrollmentEventListResponse()));

        await new HttpEnrollmentImportServiceClient(http, Configured()).GetEnrollmentEventsAsync(Tenant, Member, limit: requested);

        handler.Requests.Single().RequestUri!.Query.Should().Be($"?limit={sent}");
    }

    [Fact]
    public async Task A_null_enrollment_events_body_is_an_empty_page_and_a_failure_is_downstream_unavailable()
    {
        var (nullHttp, _) = Http(_ => Json(HttpStatusCode.OK, "null"));
        (await new HttpEnrollmentImportServiceClient(nullHttp, Configured()).GetEnrollmentEventsAsync(Tenant, Member))
            .Items.Should().BeEmpty();

        var (failHttp, _) = Http(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var act = () => new HttpEnrollmentImportServiceClient(failHttp, Configured()).GetEnrollmentEventsAsync(Tenant, Member);
        await act.Should().ThrowAsync<DownstreamUnavailableException>();
    }

    // ── accumulator-service ────────────────────────────────────────────

    [Fact]
    public async Task Accumulators_are_read_for_the_callers_tenant()
    {
        var (http, handler) = Http(_ => Json(HttpStatusCode.OK,
            new MemberAccumulatorsResponse { MemberId = Member, IndividualDeductibleUsed = 250.75m }));

        var accumulators = await new HttpAccumulatorServiceClient(http, Configured()).GetAccumulatorsAsync(Tenant, Member);

        accumulators.IndividualDeductibleUsed.Should().Be(250.75m);
        handler.Requests.Single().RequestUri!.AbsolutePath.Should().Be("/api/v1/members/M%201%2F2/accumulators");
        ShouldBeTenantScoped(handler.Requests.Single());
    }

    [Fact]
    public async Task An_empty_or_failed_accumulator_read_is_downstream_unavailable()
    {
        var (nullHttp, _) = Http(_ => Json(HttpStatusCode.OK, "null"));
        await new Func<Task>(() => new HttpAccumulatorServiceClient(nullHttp, Configured()).GetAccumulatorsAsync(Tenant, Member))
            .Should().ThrowAsync<DownstreamUnavailableException>();

        var (failHttp, _) = Http(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        (await new Func<Task>(() => new HttpAccumulatorServiceClient(failHttp, Configured()).GetAccumulatorsAsync(Tenant, Member))
            .Should().ThrowAsync<DownstreamUnavailableException>()).Which.ServiceName.Should().Be("accumulator-service");
    }
}
