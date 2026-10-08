using System.Net;
using System.Text;
using System.Text.Json;
using ClaimsService.Services;
using ClaimsService.Services.Resolution;
using CloudHealthOffice.FeeScheduleEngine.Domain;
using CloudHealthOffice.FeeScheduleEngine.Models;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace CloudHealthOffice.ClaimsService.Tests.Services.Resolution;

public class HttpFeeSchedulePricingClientTests
{
    private readonly CapturingHandler _handler = new();
    private readonly IHttpClientFactory _factory = Substitute.For<IHttpClientFactory>();

    public HttpFeeSchedulePricingClientTests()
    {
        _factory.CreateClient(UpstreamClientNames.BenefitPlanService).Returns(_ =>
            new HttpClient(_handler, disposeHandler: false) { BaseAddress = new Uri("http://benefit-plan-service:8080") });
    }

    private HttpFeeSchedulePricingClient CreateSut() =>
        new(_factory, NullLogger<HttpFeeSchedulePricingClient>.Instance);

    [Fact]
    public async Task ResolveBatchAsync_PostsToResolveRates_WithTenantHeader_AndParsesResult()
    {
        _handler.Responder = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """
                {
                  "lineResults": [
                    { "lineNumber": 1, "procedureCode": "99213", "allowedAmount": 85.5, "billedAmount": 200,
                      "feeScheduleType": "Commercial", "rateSource": "ContractedRate", "networkStatus": "InNetwork",
                      "feeScheduleId": "FS-1", "contractualAdjustment": 114.5, "adjustments": [] },
                    { "lineNumber": 2, "procedureCode": "36415", "allowedAmount": 10, "billedAmount": 30,
                      "feeScheduleType": 3, "rateSource": 4, "networkStatus": 0, "adjustments": [] }
                  ],
                  "totalAllowedAmount": 95.5
                }
                """,
                Encoding.UTF8, "application/json"),
        };

        var result = await CreateSut().ResolveBatchAsync("tenant-x", new[]
        {
            new PricingRequest { ProcedureCode = "99213", LineNumber = 1, BilledAmount = 200m, Modifiers = new[] { "25" } },
            new PricingRequest { ProcedureCode = "36415", LineNumber = 2, BilledAmount = 30m },
        });

        Assert.NotNull(result);
        Assert.Equal(2, result!.LineResults.Count);
        Assert.Equal(85.5m, result.LineResults[0].AllowedAmount);
        Assert.Equal(RateSource.ContractedRate, result.LineResults[0].RateSource);
        Assert.Equal(RateSource.PlanDefault, result.LineResults[1].RateSource); // numeric enums accepted too

        var sent = _handler.LastRequest!;
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal("/api/v1/adjudication/resolve-rates", sent.RequestUri!.AbsolutePath);
        Assert.Equal("tenant-x", Assert.Single(sent.Headers.GetValues("X-Tenant-ID")));

        using var body = JsonDocument.Parse(_handler.LastBody!);
        var lines = body.RootElement.EnumerateArray().ToList();
        Assert.Equal(2, lines.Count);
        Assert.Equal("99213", lines[0].GetProperty("procedureCode").GetString());
        Assert.Equal("25", lines[0].GetProperty("modifiers")[0].GetString());
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task ResolveBatchAsync_NonSuccess_ReturnsNull(HttpStatusCode status)
    {
        _handler.Responder = _ => new HttpResponseMessage(status);

        var result = await CreateSut().ResolveBatchAsync("tenant-x", new[] { new PricingRequest() });

        Assert.Null(result);
    }

    [Fact]
    public async Task ResolveBatchAsync_TransportFailure_ReturnsNull()
    {
        _handler.Responder = _ => throw new HttpRequestException("connection refused");

        var result = await CreateSut().ResolveBatchAsync("tenant-x", new[] { new PricingRequest() });

        Assert.Null(result);
    }

    [Fact]
    public async Task ResolveBatchAsync_MalformedBody_ReturnsNull()
    {
        _handler.Responder = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{not json", Encoding.UTF8, "application/json"),
        };

        var result = await CreateSut().ResolveBatchAsync("tenant-x", new[] { new PricingRequest() });

        Assert.Null(result);
    }

    [Fact]
    public async Task ResolveBatchAsync_CallerCancellation_Propagates()
    {
        using var cts = new CancellationTokenSource();
        _handler.Responder = _ =>
        {
            cts.Cancel();
            throw new TaskCanceledException();
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateSut().ResolveBatchAsync("tenant-x", new[] { new PricingRequest() }, cts.Token));
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Responder { get; set; } =
            _ => new HttpResponseMessage(HttpStatusCode.OK);

        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return Responder(request);
        }
    }
}
