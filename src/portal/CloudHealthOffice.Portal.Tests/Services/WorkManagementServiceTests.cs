using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using CloudHealthOffice.Portal.Services;

namespace CloudHealthOffice.Portal.Tests.Services;

/// <summary>The portal's work-management-service client: URLs, 204, refusals, outages.</summary>
public class WorkManagementServiceTests
{
    private const string Base = "http://work-management-service/api/v1";

    private static WorkManagementService Create(FakeHandler handler) => new(
        new HttpClient(handler),
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Services:WorkManagementService"] = Base,
        }).Build(),
        NullLogger<WorkManagementService>.Instance);

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private const string ItemJson = """
        {"id":"tenant-a.claim-c1","workType":"Claim","sourceId":"c1","sourceNumber":"CN-1","queueId":"q","queueName":"COB",
         "priorityScore":72.5,"priorityReason":"due in 3d","status":"Leased","assignedTo":"alice","leaseActive":true,
         "openUrl":"/claims/c1","attributes":{"primaryPendCode":"COB","amount":120.5}}
        """;

    [Fact]
    public async Task GetNext_PostsToNext_AndReadsTheItemAndOpenUrl()
    {
        var handler = new FakeHandler(_ => JsonResponse(HttpStatusCode.OK, $$"""{"item":{{ItemJson}},"resumed":false,"openUrl":"/claims/c1"}"""));

        var next = await Create(handler).GetNextAsync();

        handler.CapturedRequests.Single().Method.Should().Be(HttpMethod.Post);
        handler.CapturedUrls.Single().Should().Be($"{Base}/work/next");
        next!.OpenUrl.Should().Be("/claims/c1");
        next.Item.QueueName.Should().Be("COB");
        next.Item.Attributes.Amount.Should().Be(120.5m);
    }

    [Fact]
    public async Task GetNext_204_IsNull_WithAWorkTypeFilter()
    {
        var handler = new FakeHandler(HttpStatusCode.NoContent);

        var next = await Create(handler).GetNextAsync("Claim");

        next.Should().BeNull();
        handler.CapturedUrls.Single().Should().Be($"{Base}/work/next?workType=Claim");
    }

    [Fact]
    public async Task Assign_PostsTheUserId_ToTheItem()
    {
        string? body = null;
        var handler = new FakeHandler(request =>
        {
            body = request.Content!.ReadAsStringAsync().Result;
            return JsonResponse(HttpStatusCode.OK, ItemJson);
        });

        var item = await Create(handler).AssignAsync("tenant-a.claim-c1", "alice");

        handler.CapturedUrls.Single().Should().Be($"{Base}/work/tenant-a.claim-c1/assign");
        body.Should().Contain("\"userId\":\"alice\"");
        item.AssignedTo.Should().Be("alice");
    }

    [Theory]
    [InlineData(HttpStatusCode.Conflict)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.BadRequest)]
    public async Task Refusals_CarryTheServicesReason(HttpStatusCode status)
    {
        var handler = new FakeHandler(_ => JsonResponse(status, """{"error":"Only the holder can release this item."}"""));

        var ex = await Assert.ThrowsAsync<WorkManagementRefusedException>(() => Create(handler).ReleaseAsync("w1"));

        ex.StatusCode.Should().Be(status);
        ex.Message.Should().Be("Only the holder can release this item.");
    }

    [Fact]
    public async Task AnOutage_IsServiceUnavailable()
    {
        var handler = new FakeHandler(_ => throw new HttpRequestException("connection refused"));

        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(() => Create(handler).GetMineAsync());

        ex.ServiceName.Should().Be("Work Management Service");
    }

    [Fact]
    public async Task List_SendsTheFilters()
    {
        var handler = new FakeHandler(_ => JsonResponse(HttpStatusCode.OK, """{"page":1,"pageSize":50,"total":0,"items":[]}"""));

        await Create(handler).ListAsync(queueId: "q 1", assignedTo: "alice", pageSize: 50);

        handler.CapturedUrls.Single().Should().Be($"{Base}/work?page=1&pageSize=50&queueId=q%201&assignedTo=alice");
    }

    [Fact]
    public async Task AdminCalls_HitTheAdminRoutes()
    {
        var handler = new FakeHandler(request => request.Method == HttpMethod.Delete
            ? new HttpResponseMessage(HttpStatusCode.OK)
            : JsonResponse(HttpStatusCode.OK, request.Method == HttpMethod.Get ? "[]" : "{}"));
        var sut = Create(handler);

        await sut.GetQueueConfigsAsync();
        await sut.SaveQueueAsync(null, new WorkQueueInput { Name = "High dollar" });
        await sut.SaveQueueAsync("q1", new WorkQueueInput { Name = "High dollar" });
        await sut.DeleteQueueAsync("q1");
        await sut.ReorderQueuesAsync("Claim", ["q2", "q1"]);
        await sut.SaveMembershipAsync(null, new WorkMembershipInput { UserId = "alice", QueueId = "q1" });
        await sut.GetEffectiveQueuesAsync("alice");
        await sut.RecomputeAsync();

        handler.CapturedRequests.Select(r => $"{r.Method} {r.RequestUri!.AbsolutePath}").Should().Equal(
            "GET /api/v1/work/admin/queues",
            "POST /api/v1/work/admin/queues",
            "PUT /api/v1/work/admin/queues/q1",
            "DELETE /api/v1/work/admin/queues/q1",
            "POST /api/v1/work/admin/queues/reorder",
            "POST /api/v1/work/admin/memberships",
            "GET /api/v1/work/admin/users/alice/queues",
            "POST /api/v1/work/admin/recompute");
    }

    [Fact]
    public async Task Reconcile_502_ReturnsTheUnsuccessfulResult()
    {
        var handler = new FakeHandler(_ => JsonResponse(HttpStatusCode.BadGateway, """{"succeeded":false,"error":"claims-service unavailable"}"""));

        var result = await Create(handler).ReconcileAsync();

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be("claims-service unavailable");
    }
}
