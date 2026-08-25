using System.Net;
using System.Net.Http.Json;
using CloudHealthOffice.Infrastructure.Gateways;

namespace CloudHealthOffice.Infrastructure.Tests.Gateways;

public class HttpRemittancePostingSinkTests
{
    [Fact]
    public async Task ClaimSink_WithoutBaseAddress_IsNotFound()
    {
        var sink = new HttpClaimRemittancePostingSink(new StubFactory(new HttpClient()));
        var result = await sink.PostAsync(new RemittanceClaimPost
        {
            TenantId = "tenant-alpha",
            ClaimId = "CLM-P-1001",
            RemittanceId = "era-1",
            PaymentAmount = 10m
        });
        result.Outcome.Should().Be(RemittanceClaimPostOutcome.NotFound);
    }

    [Fact]
    public async Task ClaimSink_DoesNotCallPayerRemittanceFinalize()
    {
        var handler = new RecordingHandler(HttpStatusCode.NotFound);
        var client = new HttpClient(handler) { BaseAddress = new Uri("http://claims.test/") };
        var sink = new HttpClaimRemittancePostingSink(new StubFactory(client));

        await sink.PostAsync(new RemittanceClaimPost
        {
            TenantId = "tenant-alpha",
            ClaimId = "CLM-P-1001",
            RemittanceId = "era-1",
            PaymentAmount = 320m
        });

        handler.Path.Should().Contain("/inbound-remittance");
        handler.Path.Should().NotBe("/api/claims/CLM-P-1001/remittance");
    }

    [Fact]
    public async Task AccumulatorSink_WithoutBaseAddress_IsSkipped()
    {
        var sink = new HttpRemittanceAccumulatorSink(new StubFactory(new HttpClient()));
        var result = await sink.ApplyAsync(new RemittanceAccumulatorApply
        {
            TenantId = "tenant-alpha",
            MemberId = "U7777788888",
            ClaimId = "CLM-P-1001",
            RemittanceId = "era-1"
        });
        result.Outcome.Should().Be(RemittanceAccumulatorApplyOutcome.Skipped);
    }

    [Fact]
    public async Task AccumulatorSink_PostsAdjustWithIdempotentKey()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK);
        var client = new HttpClient(handler) { BaseAddress = new Uri("http://accum.test/") };
        var sink = new HttpRemittanceAccumulatorSink(new StubFactory(client));

        var result = await sink.ApplyAsync(new RemittanceAccumulatorApply
        {
            TenantId = "tenant-alpha",
            MemberId = "U7777788888",
            ClaimId = "CLM-P-1001",
            RemittanceId = "era-1",
            DeductibleDelta = 50m,
            OopDelta = 80m
        });

        result.Outcome.Should().Be(RemittanceAccumulatorApplyOutcome.Applied);
        handler.Path.Should().Contain("/api/v1/accumulators/");
        handler.Path.Should().Contain("/adjust");
        handler.Body.Should().Contain("835|era-1|CLM-P-1001");
        handler.Body.Should().Contain("inbound-835-posting");
        handler.Body.Should().NotContain("claims.finalized");
    }

    private sealed class StubFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;

        public StubFactory(HttpClient client) => _client = client;

        public HttpClient CreateClient(string name) => _client;
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;

        public RecordingHandler(HttpStatusCode status) => _status = status;

        public string Path { get; private set; } = string.Empty;

        public string Body { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Path = request.RequestUri?.PathAndQuery ?? string.Empty;
            if (request.Content is not null)
            {
                Body = await request.Content.ReadAsStringAsync(cancellationToken);
            }

            return new HttpResponseMessage(_status)
            {
                Content = JsonContent.Create(new { ok = true })
            };
        }
    }
}
