using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using NSubstitute;
using PaymentService.Controllers;
using PaymentService.Models;
using PaymentService.Services;
using Xunit;

namespace CloudHealthOffice.PaymentService.Tests;

/// <summary>
/// Capability 5.12b — covers <see cref="ReversalRunsController"/>'s
/// HTTP surface. Mirrors <c>PaymentRunsControllerTests</c> shape.
/// Routes under <c>/api/reversalruns</c> (controller-name routing) for
/// parity with <c>/api/paymentruns</c>.
/// </summary>
public class ReversalRunsControllerTests : IClassFixture<PaymentApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly HttpClient _client;
    private readonly IReversalRunService _runService;

    public ReversalRunsControllerTests(PaymentApiFactory factory)
    {
        _runService = factory.ReversalRunService;
        _client = factory.CreateClient();
        _client.DefaultRequestHeaders.Add("X-Tenant-ID", "test-tenant");
        _runService.ClearReceivedCalls();
    }

    private static ReversalRun PendingRun() => new()
    {
        Id = Guid.NewGuid().ToString(),
        TenantId = "test-tenant",
        ReversalRunNumber = "RR-20260502-AB12CD",
        Status = ReversalRunStatus.Pending,
        Criteria = new ReversalRunCriteria { ProviderNPI = "1234567890" },
        CreatedBy = "operator-1",
        Description = "March 2026 reversal cycle",
    };

    [Fact]
    public async Task CreateReversalRun_ValidCriteria_Returns201_AndForwardsDescription()
    {
        var run = PendingRun();
        _runService.CreateReversalRunAsync(
            Arg.Any<ReversalRunCriteria>(), Arg.Any<string?>(), Arg.Any<string?>())
            .Returns(run);

        var request = new CreateReversalRunRequest
        {
            Criteria = new ReversalRunCriteria { ProviderNPI = "1234567890" },
            CreatedBy = "operator-1",
            Description = "March 2026 reversal cycle",
        };

        var response = await _client.PostAsJsonAsync("/api/reversalruns", request, Json);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<ReversalRun>(Json);
        Assert.NotNull(created);
        Assert.Equal(ReversalRunStatus.Pending, created.Status);

        // Plumbed through: controller forwards Description to the service.
        await _runService.Received(1).CreateReversalRunAsync(
            Arg.Any<ReversalRunCriteria>(),
            "operator-1",
            "March 2026 reversal cycle");
    }

    [Fact]
    public async Task ExecuteReversalRun_Pending_Returns200Completed()
    {
        var run = PendingRun();
        run.Status = ReversalRunStatus.Completed;
        run.TotalAdjustments = 3;
        _runService.ExecuteReversalRunAsync(run.Id).Returns(run);

        var response = await _client.PostAsync($"/api/reversalruns/{run.Id}/execute", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var executed = await response.Content.ReadFromJsonAsync<ReversalRun>(Json);
        Assert.Equal(ReversalRunStatus.Completed, executed!.Status);
        Assert.Equal(3, executed.TotalAdjustments);
    }

    [Fact]
    public async Task ExecuteReversalRun_AlreadyRunning_Returns400()
    {
        _runService.ExecuteReversalRunAsync("rr-already-running")
            .Returns<ReversalRun>(_ => throw new InvalidOperationException("not in Pending"));

        var response = await _client.PostAsync("/api/reversalruns/rr-already-running/execute", null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ExecuteReversalRun_NotFound_Returns404()
    {
        _runService.ExecuteReversalRunAsync("rr-execute-missing")
            .Returns<ReversalRun>(_ =>
                throw new InvalidOperationException("Reversal run rr-execute-missing not found"));

        var response = await _client.PostAsync("/api/reversalruns/rr-execute-missing/execute", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetReversalRunById_Existing_Returns200()
    {
        var run = PendingRun();
        _runService.GetReversalRunAsync(run.Id).Returns(run);

        var response = await _client.GetAsync($"/api/reversalruns/{run.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var fetched = await response.Content.ReadFromJsonAsync<ReversalRun>(Json);
        Assert.Equal(run.Id, fetched!.Id);
    }

    [Fact]
    public async Task GetReversalRunById_Missing_Returns404()
    {
        _runService.GetReversalRunAsync("missing")
            .Returns<ReversalRun>(_ => throw new InvalidOperationException("not found"));

        var response = await _client.GetAsync("/api/reversalruns/missing");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetReversalRuns_Returns200List()
    {
        var run = PendingRun();
        _runService.GetReversalRunsAsync(Arg.Any<DateTime?>(), Arg.Any<DateTime?>())
            .Returns(new[] { run });

        var response = await _client.GetAsync("/api/reversalruns");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var list = await response.Content.ReadFromJsonAsync<List<ReversalRun>>(Json);
        Assert.NotNull(list);
        Assert.Single(list);
    }

    [Fact]
    public async Task CancelReversalRun_Pending_Returns204()
    {
        // Distinct id per test — the service substitute is class-fixture
        // scoped, so reusing ids leaks configured returns across tests.
        _runService.CancelReversalRunAsync("rr-cancel-pending").Returns(Task.CompletedTask);

        var response = await _client.PostAsync("/api/reversalruns/rr-cancel-pending/cancel", null);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task CancelReversalRun_Running_Returns400()
    {
        _runService.CancelReversalRunAsync("rr-cancel-running")
            .Returns<Task>(_ => throw new InvalidOperationException("Cannot cancel a running reversal run"));

        var response = await _client.PostAsync("/api/reversalruns/rr-cancel-running/cancel", null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CancelReversalRun_NotFound_Returns404()
    {
        _runService.CancelReversalRunAsync("rr-cancel-missing")
            .Returns<Task>(_ =>
                throw new InvalidOperationException("Reversal run rr-cancel-missing not found"));

        var response = await _client.PostAsync("/api/reversalruns/rr-cancel-missing/cancel", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
