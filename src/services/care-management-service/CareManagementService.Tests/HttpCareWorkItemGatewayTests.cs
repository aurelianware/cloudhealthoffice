using System.Net;
using System.Text.Json.Nodes;
using CareManagementService.Models;
using CareManagementService.Services.Work;
using Microsoft.Extensions.Logging.Abstractions;

namespace CareManagementService.Tests;

/// <summary>
/// The work-queue gateway's contract: it carries the case's own tenant, and
/// it never throws. The care record is the system of record, so a queue that
/// is down costs the activity its work item and nothing else.
/// </summary>
public class HttpCareWorkItemGatewayTests
{
    private static CareWorkItemCommand Command(CareAcuity acuity = CareAcuity.High) => new()
    {
        TenantId = "t1",
        CaseId = "case-1",
        CaseNumber = "CM-1",
        MemberId = "M1",
        Program = CareProgram.TransitionsOfCare,
        Acuity = acuity,
        Activity = new CareActivity
        {
            Id = "a1",
            Description = "Call member within 48 hours",
            ActivityType = "OutreachCall",
            AssignedTo = "cm-7",
        },
    };

    private static (HttpCareWorkItemGateway Gateway, StubHandler Handler) Build(
        HttpStatusCode status = HttpStatusCode.OK, string? body = null, Exception? throws = null)
    {
        var handler = new StubHandler(status, body, throws);
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(HttpCareWorkItemGateway.HttpClientName))
               .Returns(() => new HttpClient(handler) { BaseAddress = new Uri("http://work-management/") });

        return (new HttpCareWorkItemGateway(factory.Object, NullLogger<HttpCareWorkItemGateway>.Instance), handler);
    }

    [Fact]
    public async Task A_queued_activity_returns_the_work_item_id()
    {
        var (gateway, _) = Build(body: """{"id":"wi-99"}""");

        var id = await gateway.EnqueueAsync(Command());

        id.Should().Be("wi-99");
    }

    [Fact]
    public async Task The_cases_own_tenant_travels_on_the_request_rather_than_an_ambient_one()
    {
        var (gateway, handler) = Build(body: """{"id":"wi-99"}""");

        await gateway.EnqueueAsync(Command());

        handler.LastRequest!.Headers.GetValues("X-Tenant-ID").Should().ContainSingle().Which.Should().Be("t1");
    }

    [Fact]
    public async Task The_activity_is_sent_with_the_case_as_its_reference_and_the_programme_as_its_queue()
    {
        var (gateway, handler) = Build(body: """{"id":"wi-99"}""");

        await gateway.EnqueueAsync(Command());

        var payload = JsonNode.Parse(handler.LastBody!)!;
        payload["workType"]!.GetValue<string>().Should().Be("care-activity");
        payload["externalId"]!.GetValue<string>().Should().Be("case-1:a1");
        payload["subjectId"]!.GetValue<string>().Should().Be("M1");
        payload["title"]!.GetValue<string>().Should().Be("Call member within 48 hours");
        payload["reference"]!.GetValue<string>().Should().Be("CM-1");
        payload["queue"]!.GetValue<string>().Should().Be("care-management/TransitionsOfCare");
    }

    [Theory]
    [InlineData(CareAcuity.Critical, "urgent")]
    [InlineData(CareAcuity.High, "high")]
    [InlineData(CareAcuity.Moderate, "normal")]
    [InlineData(CareAcuity.Low, "low")]
    public async Task Acuity_drives_the_queues_priority(CareAcuity acuity, string expected)
    {
        var (gateway, handler) = Build(body: """{"id":"wi-99"}""");

        await gateway.EnqueueAsync(Command(acuity));

        JsonNode.Parse(handler.LastBody!)!["priority"]!.GetValue<string>().Should().Be(expected);
    }

    [Fact]
    public async Task A_refused_queue_yields_no_work_item_rather_than_an_error()
    {
        var (gateway, _) = Build(HttpStatusCode.ServiceUnavailable);

        var id = await gateway.EnqueueAsync(Command());

        id.Should().BeNull();
    }

    [Fact]
    public async Task An_unreachable_queue_yields_no_work_item_rather_than_an_error()
    {
        var (gateway, _) = Build(throws: new HttpRequestException("no route to host"));

        var id = await gateway.EnqueueAsync(Command());

        id.Should().BeNull("the care record stands whether or not the queue took the item");
    }

    [Fact]
    public async Task A_cancelled_enqueue_is_not_swallowed_as_a_queue_failure()
    {
        var (gateway, _) = Build(throws: new OperationCanceledException());
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var enqueue = async () => await gateway.EnqueueAsync(Command(), cts.Token);

        await enqueue.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Completing_a_work_item_posts_to_it_with_the_tenant_and_the_outcome()
    {
        var (gateway, handler) = Build();

        await gateway.CompleteAsync("t1", "wi-99", "Reached", default);

        handler.LastRequest!.RequestUri!.AbsolutePath.Should().Be("/api/work/wi-99/complete");
        handler.LastRequest.Headers.GetValues("X-Tenant-ID").Should().ContainSingle().Which.Should().Be("t1");
        JsonNode.Parse(handler.LastBody!)!["outcome"]!.GetValue<string>().Should().Be("Reached");
    }

    [Fact]
    public async Task Completing_nothing_sends_nothing()
    {
        var (gateway, handler) = Build();

        await gateway.CompleteAsync("t1", "   ", null, default);

        handler.LastRequest.Should().BeNull();
    }

    [Fact]
    public async Task A_refused_completion_does_not_throw()
    {
        var (gateway, _) = Build(HttpStatusCode.InternalServerError);

        var complete = async () => await gateway.CompleteAsync("t1", "wi-99", null, default);

        await complete.Should().NotThrowAsync();
    }

    [Fact]
    public async Task An_unreachable_queue_does_not_fail_a_completion()
    {
        var (gateway, _) = Build(throws: new HttpRequestException("no route to host"));

        var complete = async () => await gateway.CompleteAsync("t1", "wi-99", null, default);

        await complete.Should().NotThrowAsync();
    }

    [Fact]
    public async Task The_null_gateway_records_nothing_and_queues_nothing()
    {
        var gateway = new NullCareWorkItemGateway();

        (await gateway.EnqueueAsync(Command())).Should().BeNull();
        await gateway.Invoking(g => g.CompleteAsync("t1", "wi-99", null)).Should().NotThrowAsync();
    }

    private sealed class StubHandler(HttpStatusCode status, string? body, Exception? throws) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);

            if (throws is not null) throw throws;

            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body ?? "{}", System.Text.Encoding.UTF8, "application/json"),
            };
        }
    }
}
