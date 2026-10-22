using System.Net;
using System.Text;
using EligibilityService.Adapters;
using EligibilityService.Controllers;
using EligibilityService.Models;
using EligibilityService.Repositories;
using EligibilityService.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace CloudHealthOffice.EligibilityService.Tests;

public class CoverageLookupFailureTests
{
    private static readonly DateTime Date = new(2026, 10, 22);

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct);
    }

    private static (ChoEligibilityAdapter Adapter, TemporalEligibilityService Temporal, EligibilityServiceImpl Service) Build(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Services:CoverageService"] = "http://coverage.test/api/v1",
        }).Build();
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("EligibilityDefault").Returns(_ => new HttpClient(new Handler(send)));
        return (
            new ChoEligibilityAdapter(factory, config, NullLogger<ChoEligibilityAdapter>.Instance),
            new TemporalEligibilityService(factory, new StubAccumulatorClient(), config, NullLogger<TemporalEligibilityService>.Instance),
            new EligibilityServiceImpl(Substitute.For<IEligibilityRepository>(), new HttpClient(new Handler(send)),
                NullLogger<EligibilityServiceImpl>.Instance, config,
                new EligibilityAdapterFactory([], factory, config, NullLogger<EligibilityAdapterFactory>.Instance)));
    }

    private static Task<HttpResponseMessage> Response(HttpStatusCode status, string body) => Task.FromResult(
        new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });

    [Theory]
    [InlineData(401, "{}")]
    [InlineData(403, "{}")]
    [InlineData(429, "{}")]
    [InlineData(500, "{}")]
    [InlineData(503, "{}")]
    [InlineData(200, "")]
    [InlineData(200, "null")]
    [InlineData(200, "{invalid")]
    [InlineData(200, "{}")]
    public async Task AllCoveragePaths_RejectFailuresInsteadOfReturningIneligibility(int status, string body)
    {
        var (adapter, temporal, service) = Build((_, _) => Response((HttpStatusCode)status, body));
        await Assert.ThrowsAsync<CoverageLookupUnavailableException>(() => adapter.VerifyEligibilityAsync(
            new EligibilityAdapterRequest { TenantId = "t1", SubscriberId = "M1", ServiceDate = Date }));
        await Assert.ThrowsAsync<CoverageLookupUnavailableException>(() => temporal.GetAsOfAsync("t1", "M1", Date));
        await Assert.ThrowsAsync<CoverageLookupUnavailableException>(() => service.QuickEligibilityCheckAsync("t1", "M1", null, Date));
    }

    [Theory]
    [InlineData(404, "{}")]
    [InlineData(200, "[]")]
    public async Task OnlyDocumentedNoCoverageResponses_ReturnNegativeDeterminations(int status, string body)
    {
        var (adapter, temporal, service) = Build((_, _) => Response((HttpStatusCode)status, body));
        var result = await adapter.VerifyEligibilityAsync(new EligibilityAdapterRequest { TenantId = "t1", SubscriberId = "M1", ServiceDate = Date });
        Assert.False(result.IsEligible);
        Assert.Empty((await temporal.GetAsOfAsync("t1", "M1", Date)).Coverages);
        Assert.False((await service.QuickEligibilityCheckAsync("t1", "M1", null, Date)).IsActive);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TransportAndTimeoutFailures_AreUnavailable(bool timeout)
    {
        var (adapter, temporal, service) = Build((_, _) => throw (timeout
            ? (Exception)new TaskCanceledException("upstream timeout") : new HttpRequestException("connection failed")));
        await Assert.ThrowsAsync<CoverageLookupUnavailableException>(() => adapter.VerifyEligibilityAsync(new EligibilityAdapterRequest { SubscriberId = "M1" }));
        await Assert.ThrowsAsync<CoverageLookupUnavailableException>(() => temporal.GetAsOfAsync("t1", "M1", Date));
        await Assert.ThrowsAsync<CoverageLookupUnavailableException>(() => service.QuickEligibilityCheckAsync("t1", "M1", null, Date));
    }

    [Fact]
    public async Task CallerCancellation_PropagatesInsteadOfBecomingUnavailableOrIneligible()
    {
        var (adapter, temporal, _) = Build((_, ct) => Task.FromCanceled<HttpResponseMessage>(ct));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => adapter.VerifyEligibilityAsync(new EligibilityAdapterRequest { SubscriberId = "M1" }, cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => temporal.GetAsOfAsync("t1", "M1", Date, cts.Token));
    }

    [Fact]
    public async Task TemporalEndpoint_Returns503ProblemWithoutUpstreamDetails()
    {
        var (_, temporal, _) = Build((_, _) => throw new HttpRequestException("private-upstream-host"));
        var http = new DefaultHttpContext();
        http.Items["TenantId"] = "t1";
        var controller = new TemporalEligibilityController(temporal, NullLogger<TemporalEligibilityController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };

        var result = await controller.Get("M1", Date);

        var obj = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(503, obj.StatusCode);
        var problem = Assert.IsType<ProblemDetails>(obj.Value);
        Assert.DoesNotContain("private-upstream-host", problem.Detail);
        Assert.True(problem.Extensions.ContainsKey("traceId"));
    }
}
