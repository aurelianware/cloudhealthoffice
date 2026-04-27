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

public class PaymentRunsControllerTests : IClassFixture<PaymentApiFactory>
{
    // Match the server's wire format (string enums via JsonStringEnumConverter
    // registered by AddCloudHealthOfficeJsonOptions).
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly HttpClient _client;
    private readonly IPaymentRunService _runService;

    public PaymentRunsControllerTests(PaymentApiFactory factory)
    {
        _runService = factory.PaymentRunService;
        _client = factory.CreateClient();
        _client.DefaultRequestHeaders.Add("X-Tenant-ID", "test-tenant");
    }

    // ═══════════════════════════════════════════════════════════════════
    // HELPERS
    // ═══════════════════════════════════════════════════════════════════

    private static PaymentRun CreatePendingRun()
    {
        return new PaymentRun
        {
            Id = Guid.NewGuid().ToString(),
            TenantId = "test-tenant",
            PaymentRunNumber = "PR-20260315-A1B2C3",
            Status = PaymentRunStatus.Pending,
            Criteria = new PaymentRunCriteria
            {
                LineOfBusiness = LineOfBusiness.Commercial,
                GroupByProvider = true
            },
            CreatedBy = "admin@test.com",
            NextCheckNumber = 1000000
        };
    }

    // ═══════════════════════════════════════════════════════════════════
    // FEATURE 1: CREATE PAYMENT RUN (FROM ADJUDICATED CLAIMS)
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task CreatePaymentRun_ValidCriteria_Returns201WithPaymentRunId()
    {
        var run = CreatePendingRun();

        _runService.CreatePaymentRunAsync(Arg.Any<PaymentRunCriteria>(), Arg.Any<string?>())
            .Returns(run);

        var request = new CreatePaymentRunRequest
        {
            Criteria = new PaymentRunCriteria { LineOfBusiness = LineOfBusiness.Commercial },
            CreatedBy = "admin@test.com"
        };

        var response = await _client.PostAsJsonAsync("/api/paymentruns", request, Json);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<PaymentRun>(Json);
        Assert.NotNull(created);
        Assert.NotEmpty(created.Id);
        Assert.Equal(PaymentRunStatus.Pending, created.Status);
    }

    // ═══════════════════════════════════════════════════════════════════
    // FEATURE 2: EXECUTE PAYMENT RUN
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ExecutePaymentRun_PendingRun_Returns200WithCompletedStatus()
    {
        var run = CreatePendingRun();
        run.Status = PaymentRunStatus.Completed;
        run.TotalClaims = 5;
        run.TotalPaymentAmount = 12500.00m;
        run.PaymentIds = new List<string> { "pay-1", "pay-2" };

        _runService.ExecutePaymentRunAsync(Arg.Any<string>()).Returns(run);

        var response = await _client.PostAsync($"/api/paymentruns/{run.Id}/execute", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var executed = await response.Content.ReadFromJsonAsync<PaymentRun>(Json);
        Assert.NotNull(executed);
        Assert.Equal(PaymentRunStatus.Completed, executed.Status);
        Assert.Equal(5, executed.TotalClaims);
        Assert.Equal(12500.00m, executed.TotalPaymentAmount);
    }

    [Fact]
    public async Task ExecutePaymentRun_NonPendingRun_Returns400()
    {
        _runService.ExecutePaymentRunAsync(Arg.Any<string>())
            .Returns<PaymentRun>(x => throw new InvalidOperationException("Payment run is not in Pending status"));

        var response = await _client.PostAsync("/api/paymentruns/run-not-pending/execute", null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ═══════════════════════════════════════════════════════════════════
    // FEATURE 3: CREATE AND EXECUTE (ADJUDICATED CLAIMS → PAYMENT)
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task CreateAndExecute_AdjudicatedClaims_Returns200WithPayments()
    {
        var pendingRun = CreatePendingRun();
        var completedRun = CreatePendingRun();
        completedRun.Status = PaymentRunStatus.Completed;
        completedRun.TotalClaims = 3;
        completedRun.TotalPaymentAmount = 7500.00m;
        completedRun.PaymentIds = new List<string> { "pay-1" };

        _runService.CreatePaymentRunAsync(Arg.Any<PaymentRunCriteria>(), Arg.Any<string?>())
            .Returns(pendingRun);
        _runService.ExecutePaymentRunAsync(pendingRun.Id)
            .Returns(completedRun);

        var request = new CreatePaymentRunRequest
        {
            Criteria = new PaymentRunCriteria { LineOfBusiness = LineOfBusiness.Commercial },
            CreatedBy = "admin@test.com"
        };

        var response = await _client.PostAsJsonAsync("/api/paymentruns/execute", request, Json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<PaymentRun>(Json);
        Assert.NotNull(result);
        Assert.Equal(PaymentRunStatus.Completed, result.Status);
        Assert.NotEmpty(result.PaymentIds);
    }

    // ═══════════════════════════════════════════════════════════════════
    // FEATURE 4: UNADJUDICATED CLAIM REJECTION
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ExecutePaymentRun_NoApprovedClaims_CompletesWithWarning()
    {
        // When no approved claims match, the run completes with a warning
        // (the service only fetches status=5/Approved claims)
        var run = CreatePendingRun();
        run.Status = PaymentRunStatus.Completed;
        run.TotalClaims = 0;
        run.TotalPaymentAmount = 0m;
        run.Warnings = new List<string> { "No approved claims found matching criteria" };

        _runService.ExecutePaymentRunAsync(Arg.Any<string>()).Returns(run);

        var response = await _client.PostAsync($"/api/paymentruns/{run.Id}/execute", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<PaymentRun>(Json);
        Assert.NotNull(result);
        Assert.Equal(PaymentRunStatus.Completed, result.Status);
        Assert.Equal(0, result.TotalClaims);
        Assert.Contains(result.Warnings, w => w.Contains("No approved claims"));
    }

    // ═══════════════════════════════════════════════════════════════════
    // FEATURE 5: PAYMENT RUN STATUS TRACKING
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task GetPaymentRunById_ExistingRun_Returns200()
    {
        var run = CreatePendingRun();

        _runService.GetPaymentRunAsync(run.Id).Returns(run);

        var response = await _client.GetAsync($"/api/paymentruns/{run.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var returned = await response.Content.ReadFromJsonAsync<PaymentRun>(Json);
        Assert.NotNull(returned);
        Assert.Equal(run.Id, returned.Id);
        Assert.Equal(PaymentRunStatus.Pending, returned.Status);
    }

    [Fact]
    public async Task GetPaymentRunById_NonexistentRun_Returns404()
    {
        _runService.GetPaymentRunAsync("nonexistent")
            .Returns<PaymentRun>(x => throw new InvalidOperationException("Payment run nonexistent not found"));

        var response = await _client.GetAsync("/api/paymentruns/nonexistent");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetPaymentRuns_ReturnsList()
    {
        var run1 = CreatePendingRun();
        var run2 = CreatePendingRun();
        run2.Status = PaymentRunStatus.Completed;

        _runService.GetPaymentRunsAsync(Arg.Any<DateTime?>(), Arg.Any<DateTime?>())
            .Returns(new List<PaymentRun> { run1, run2 });

        var response = await _client.GetAsync("/api/paymentruns");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var results = await response.Content.ReadFromJsonAsync<List<PaymentRun>>(Json);
        Assert.NotNull(results);
        Assert.Equal(2, results.Count);
    }

    // ═══════════════════════════════════════════════════════════════════
    // FEATURE 6: CANCEL PAYMENT RUN
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task CancelPaymentRun_PendingRun_Returns204()
    {
        _runService.CancelPaymentRunAsync("run-to-cancel")
            .Returns(Task.CompletedTask);

        var response = await _client.PostAsync("/api/paymentruns/run-to-cancel/cancel", null);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task CancelPaymentRun_RunningRun_Returns400()
    {
        _runService.CancelPaymentRunAsync("run-running")
            .Returns<Task>(x => throw new InvalidOperationException("Cannot cancel a running payment run"));

        var response = await _client.PostAsync("/api/paymentruns/run-running/cancel", null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
