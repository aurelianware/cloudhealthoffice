using System.Net;
using System.Text;
using EligibilityService.Adapters;
using EligibilityService.Models;
using EligibilityService.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace CloudHealthOffice.EligibilityService.Tests;

public class BatchEligibilityServiceTests
{
    private static BatchEligibilityService CreateService(
        IEligibilityAdapter adapter,
        out InMemoryBatchJobStore store,
        out InMemoryBatchQueue queue)
    {
        store = new InMemoryBatchJobStore();
        queue = new InMemoryBatchQueue();

        // Handler returns 500 so the factory falls back to its default ("cho")
        var handler = new FailingHandler();
        var httpFactory = Substitute.For<IHttpClientFactory>();
        httpFactory.CreateClient("EligibilityDefault").Returns(_ => new HttpClient(handler));

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Services:TenantService"] = "http://localhost:0/api/v1"
            })
            .Build();

        var factory = new EligibilityAdapterFactory(
            new[] { adapter },
            httpFactory,
            config,
            Substitute.For<ILogger<EligibilityAdapterFactory>>());

        return new BatchEligibilityService(
            store,
            queue,
            factory,
            Substitute.For<ILogger<BatchEligibilityService>>());
    }

    [Fact]
    public async Task Submit_SmallCsvBatch_RunsInlineAndCompletes()
    {
        var adapter = new StubAdapter(mb => mb.StartsWith("OK"));
        var svc = CreateService(adapter, out var store, out _);

        var csv = new StringBuilder()
            .AppendLine("memberId,serviceDate")
            .AppendLine("OK-1,2026-01-15")
            .AppendLine("OK-2,2026-01-15")
            .AppendLine("FAIL-1,2026-01-15")
            .ToString();

        using var body = new MemoryStream(Encoding.UTF8.GetBytes(csv));
        var job = await svc.SubmitAsync("tenant-1", body, "text/csv");

        Assert.Equal(BatchJobStatus.Completed, job.Status);
        Assert.Equal(3, job.TotalRows);
        Assert.Equal(3, job.ProcessedRows);
        Assert.Equal(2, job.SucceededRows);
        Assert.Equal(1, job.FailedRows);
        Assert.False(job.Queued);
        Assert.NotNull(job.ResultFileUrl);

        var resultBytes = await store.GetResultAsync("tenant-1", job.Id);
        Assert.NotNull(resultBytes);
        var resultCsv = Encoding.UTF8.GetString(resultBytes!);
        Assert.Contains("OK-1", resultCsv);
        Assert.Contains("FAIL-1", resultCsv);
        Assert.Contains("True", resultCsv);
        Assert.Contains("False", resultCsv);
    }

    [Fact]
    public async Task Submit_LargeBatch_Returns202AndEnqueues()
    {
        var adapter = new StubAdapter(_ => true);
        var svc = CreateService(adapter, out _, out var queue);

        var sb = new StringBuilder();
        sb.AppendLine("memberId,serviceDate");
        for (var i = 1; i <= 1000; i++)
            sb.AppendLine($"MBR-{i},2026-01-15");

        using var body = new MemoryStream(Encoding.UTF8.GetBytes(sb.ToString()));
        var job = await svc.SubmitAsync("tenant-1", body, "text/csv");

        Assert.Equal(BatchJobStatus.Queued, job.Status);
        Assert.True(job.Queued);
        Assert.Equal(1000, job.TotalRows);
        Assert.Equal(0, job.ProcessedRows); // not yet processed

        // The queue should have received exactly one message
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var enumerator = queue.ReadAllAsync(cts.Token).GetAsyncEnumerator();
        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal(job.Id, enumerator.Current.JobId);
        Assert.Equal("tenant-1", enumerator.Current.TenantId);
    }

    [Fact]
    public async Task ProcessJob_LargeBatch_CompletesAllRows()
    {
        var adapter = new StubAdapter(_ => true);
        var svc = CreateService(adapter, out _, out _);

        var sb = new StringBuilder();
        sb.AppendLine("memberId,serviceDate");
        for (var i = 1; i <= 1000; i++)
            sb.AppendLine($"MBR-{i},2026-01-15");

        using var body = new MemoryStream(Encoding.UTF8.GetBytes(sb.ToString()));
        var job = await svc.SubmitAsync("tenant-1", body, "text/csv");

        await svc.ProcessJobAsync("tenant-1", job.Id);

        var final = await svc.GetJobAsync("tenant-1", job.Id);
        Assert.NotNull(final);
        Assert.Equal(BatchJobStatus.Completed, final!.Status);
        Assert.Equal(1000, final.ProcessedRows);
        Assert.Equal(1000, final.SucceededRows);
        Assert.NotNull(final.ResultFileUrl);
    }

    [Fact]
    public async Task ProcessJob_CalledTwice_IsIdempotent()
    {
        var adapter = new StubAdapter(_ => true);
        var svc = CreateService(adapter, out _, out _);

        var csv = "memberId,serviceDate\nMBR-1,2026-01-15\n";
        using var body = new MemoryStream(Encoding.UTF8.GetBytes(csv));
        var job = await svc.SubmitAsync("tenant-1", body, "text/csv");

        // First run already completed synchronously — calling again must not change state.
        await svc.ProcessJobAsync("tenant-1", job.Id);
        var final = await svc.GetJobAsync("tenant-1", job.Id);

        Assert.Equal(BatchJobStatus.Completed, final!.Status);
        Assert.Equal(1, final.ProcessedRows);
    }

    [Fact]
    public async Task Submit_InvalidCsv_ThrowsArgumentException()
    {
        var adapter = new StubAdapter(_ => true);
        var svc = CreateService(adapter, out _, out _);

        using var body = new MemoryStream(Encoding.UTF8.GetBytes("wrongHeader\nvalue\n"));
        await Assert.ThrowsAsync<ArgumentException>(
            () => svc.SubmitAsync("tenant-1", body, "text/csv"));
    }

    [Fact]
    public async Task Submit_RowWithInvalidServiceDate_ThrowsArgumentException()
    {
        var adapter = new StubAdapter(_ => true);
        var svc = CreateService(adapter, out _, out _);

        var csv = "memberId,serviceDate\nMBR-1,not-a-date\n";
        using var body = new MemoryStream(Encoding.UTF8.GetBytes(csv));
        await Assert.ThrowsAsync<ArgumentException>(
            () => svc.SubmitAsync("tenant-1", body, "text/csv"));
    }

    [Fact]
    public async Task Submit_RowWithBothIds_PassesSubscriberIdToAdapter()
    {
        // Identifier precedence: when both memberId and subscriberId are
        // supplied, the subscriberId is what travels to the adapter.
        string? capturedSubscriberId = null;
        var adapter = new CapturingAdapter(req => capturedSubscriberId = req.SubscriberId);
        var svc = CreateService(adapter, out _, out _);

        var csv = "memberId,subscriberId,serviceDate\nMBR-1,SUB-42,2026-01-15\n";
        using var body = new MemoryStream(Encoding.UTF8.GetBytes(csv));
        await svc.SubmitAsync("tenant-1", body, "text/csv");

        Assert.Equal("SUB-42", capturedSubscriberId);
    }

    private class CapturingAdapter : IEligibilityAdapter
    {
        private readonly Action<EligibilityAdapterRequest> _capture;
        public string Platform => "cho";
        public CapturingAdapter(Action<EligibilityAdapterRequest> capture) { _capture = capture; }

        public Task<EligibilityAdapterResponse> VerifyEligibilityAsync(
            EligibilityAdapterRequest request, CancellationToken ct = default)
        {
            _capture(request);
            return Task.FromResult(new EligibilityAdapterResponse
            {
                IsEligible = true,
                StatusCode = "1"
            });
        }
    }

    // ── Test helpers ──────────────────────────────────────────────────────

    private class StubAdapter : IEligibilityAdapter
    {
        private readonly Func<string, bool> _isEligible;
        public string Platform => "cho";

        public StubAdapter(Func<string, bool> isEligible)
        {
            _isEligible = isEligible;
        }

        public Task<EligibilityAdapterResponse> VerifyEligibilityAsync(
            EligibilityAdapterRequest request, CancellationToken ct = default)
        {
            var eligible = _isEligible(request.SubscriberId);
            return Task.FromResult(new EligibilityAdapterResponse
            {
                IsEligible = eligible,
                StatusCode = eligible ? "1" : "6",
                PlanId = eligible ? "PLAN-X" : null,
                GroupNumber = eligible ? "GRP-X" : null,
                CoverageLevel = eligible ? "IND" : null
            });
        }
    }

    private class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
    }
}
