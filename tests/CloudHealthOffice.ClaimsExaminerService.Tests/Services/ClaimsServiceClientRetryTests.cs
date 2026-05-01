using System.Net;
using System.Text;
using ClaimsExaminerService.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CloudHealthOffice.ClaimsExaminerService.Tests.Services;

/// <summary>
/// Capability 5.9 / Plan-First Decision 16 / D.1 — verifies bounded
/// retry-on-404 in <see cref="ClaimsServiceClient.GetClaimAsync"/>.
/// Mitigates the AiExaminationStage emission → PersistenceStage
/// persistence race: the stage emits at Order=600; the claim isn't
/// persisted until Order=999. If the consumer races persistence, the
/// initial GET 404s. Three attempts × 250 ms cover the gap; if the
/// claim is still missing after retry exhaustion, log and return null
/// (consumer commits offset, claim stays pended-without-AI).
/// </summary>
public class ClaimsServiceClientRetryTests
{
    private const string ClaimId = "claim-race-1";
    private const string TenantId = "tenant-race-1";

    [Fact]
    public async Task Returns_null_after_max_attempts_when_404_persists()
    {
        var handler = new RecordingHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.NotFound));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://claims-service") };
        var sut = new ClaimsServiceClient(http, NullLogger<ClaimsServiceClient>.Instance);

        var result = await sut.GetClaimAsync(ClaimId, TenantId, CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(ClaimsServiceClient.GetClaimNotFoundMaxAttempts, handler.RequestCount);
    }

    [Fact]
    public async Task Succeeds_when_claim_appears_on_second_attempt()
    {
        var attempts = 0;
        var json = """{"id":"claim-race-1","tenantId":"tenant-race-1","billingProviderNPI":"1234567890","claimLines":[]}""";
        var handler = new RecordingHandler(_ =>
        {
            attempts++;
            if (attempts == 1)
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://claims-service") };
        var sut = new ClaimsServiceClient(http, NullLogger<ClaimsServiceClient>.Instance);

        var result = await sut.GetClaimAsync(ClaimId, TenantId, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(ClaimId, result!.Id);
        Assert.Equal(2, handler.RequestCount);  // 1 retry then success
    }

    [Fact]
    public async Task Succeeds_on_first_attempt_no_retry_invoked()
    {
        // RequestCount==1 is the actual behavioral guarantee — no retry
        // path was taken. Wall-clock-elapsed checks against the retry
        // delay are CI-flaky (scheduler pauses, GC) even when zero delay
        // actually occurred.
        var json = """{"id":"claim-race-1","tenantId":"tenant-race-1","billingProviderNPI":"1234567890","claimLines":[]}""";
        var handler = new RecordingHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://claims-service") };
        var sut = new ClaimsServiceClient(http, NullLogger<ClaimsServiceClient>.Instance);

        var result = await sut.GetClaimAsync(ClaimId, TenantId, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task Non_404_errors_throw_immediately_without_retry()
    {
        // EnsureSuccessStatusCode on 500 — propagates as HttpRequestException.
        // No retry behavior on transport errors; the upstream Anthropic
        // failure path catches HttpRequestException at the orchestrator.
        var handler = new RecordingHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.InternalServerError));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://claims-service") };
        var sut = new ClaimsServiceClient(http, NullLogger<ClaimsServiceClient>.Instance);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => sut.GetClaimAsync(ClaimId, TenantId, CancellationToken.None));

        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task Cancellation_token_propagates_during_retry_delay()
    {
        using var cts = new CancellationTokenSource();
        var handler = new RecordingHandler(_ =>
        {
            // After first 404, cancel — the Task.Delay should observe it.
            cts.CancelAfter(50);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://claims-service") };
        var sut = new ClaimsServiceClient(http, NullLogger<ClaimsServiceClient>.Instance);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => sut.GetClaimAsync(ClaimId, TenantId, cts.Token));
    }

    [Fact]
    public void Constants_match_documented_mitigation_envelope()
    {
        // Plan-First D.1 ratification — "3 attempts × 250 ms" is the
        // architecture doc's stated envelope. Pinning the constants
        // protects against silent drift.
        Assert.Equal(3, ClaimsServiceClient.GetClaimNotFoundMaxAttempts);
        Assert.Equal(TimeSpan.FromMilliseconds(250), ClaimsServiceClient.GetClaimNotFoundRetryDelay);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        public int RequestCount { get; private set; }

        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestCount++;
            return Task.FromResult(_responder(request));
        }
    }
}
