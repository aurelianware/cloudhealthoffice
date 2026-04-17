using System.Net;
using System.Text;
using System.Text.Json;
using EligibilityService.Models;
using EligibilityService.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace CloudHealthOffice.EligibilityService.Tests;

public class TemporalEligibilityServiceTests
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private static TemporalEligibilityService CreateService(
        HttpMessageHandler handler,
        IAccumulatorClient? accumulators = null)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("EligibilityDefault").Returns(new HttpClient(handler));

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Services:CoverageService"] = "http://localhost:9000/api/v1"
            })
            .Build();

        return new TemporalEligibilityService(
            factory,
            accumulators ?? new StubAccumulatorClient(),
            configuration,
            Substitute.For<ILogger<TemporalEligibilityService>>());
    }

    [Fact]
    public async Task GetAsOf_SingleActiveCoverage_ReturnsOneWithPrimaryCobOrder()
    {
        var coverage = new[]
        {
            new
            {
                id = "cov-1",
                memberId = "MBR-1",
                groupNumber = "GRP-100",
                planId = "PLAN-A",
                coverageLevel = "FAM",
                insuranceLineCode = "HLT",
                effectiveDate = "2025-01-01",
                terminationDate = (string?)null,
                status = 1,
                lineOfBusiness = 1
            }
        };
        var handler = new StaticHandler(HttpStatusCode.OK, JsonSerializer.Serialize(coverage, JsonOpts));
        var svc = CreateService(handler);

        var result = await svc.GetAsOfAsync("t1", "MBR-1", new DateTime(2026, 2, 1));

        Assert.Single(result.Coverages);
        Assert.Equal(1, result.Coverages[0].CobOrder);
        Assert.Equal("P", result.Coverages[0].CoverageSequence);
        Assert.Equal("PLAN-A", result.Coverages[0].PlanId);
        Assert.Equal("stub", result.Coverages[0].Accumulators?.Source);
    }

    [Fact]
    public async Task GetAsOf_MultiCoverage_OrdersByPrimaryIndicator()
    {
        // Coverage A has otherInsurance.IsPrimaryPayer = true  → pushes A to secondary
        // Coverage B has no COB markings                       → B wins primary
        var coverages = new object[]
        {
            new
            {
                id = "cov-A",
                memberId = "MBR-1",
                groupNumber = "GRP-A",
                planId = "PLAN-A",
                effectiveDate = "2025-01-01",
                status = 1,
                lineOfBusiness = 1,
                otherInsurance = new { isPrimaryPayer = true, payerName = "Other" }
            },
            new
            {
                id = "cov-B",
                memberId = "MBR-1",
                groupNumber = "GRP-B",
                planId = "PLAN-B",
                effectiveDate = "2025-03-01",
                status = 1,
                lineOfBusiness = 1
            }
        };
        var handler = new StaticHandler(HttpStatusCode.OK, JsonSerializer.Serialize(coverages, JsonOpts));
        var svc = CreateService(handler);

        var result = await svc.GetAsOfAsync("t1", "MBR-1", new DateTime(2026, 1, 1));

        Assert.Equal(2, result.Coverages.Count);
        Assert.Equal("PLAN-B", result.Coverages[0].PlanId);
        Assert.Equal(1, result.Coverages[0].CobOrder);
        Assert.Equal("P", result.Coverages[0].CoverageSequence);
        Assert.Equal("PLAN-A", result.Coverages[1].PlanId);
        Assert.Equal(2, result.Coverages[1].CobOrder);
        Assert.Equal("S", result.Coverages[1].CoverageSequence);
    }

    [Fact]
    public async Task GetAsOf_RetroEffectiveCoverage_IsMarkedRetroactive()
    {
        var coverage = new[]
        {
            new
            {
                id = "cov-retro",
                memberId = "MBR-1",
                groupNumber = "GRP-R",
                planId = "PLAN-R",
                // Effective date in the past, termination in the future
                effectiveDate = DateTime.UtcNow.AddMonths(-6).ToString("yyyy-MM-dd"),
                terminationDate = (string?)null,
                status = 1,
                lineOfBusiness = 1
            }
        };
        var handler = new StaticHandler(HttpStatusCode.OK, JsonSerializer.Serialize(coverage, JsonOpts));
        var svc = CreateService(handler);

        // Service date is within the retro window
        var result = await svc.GetAsOfAsync("t1", "MBR-1", DateTime.UtcNow.AddMonths(-3));

        Assert.Single(result.Coverages);
        Assert.True(result.Coverages[0].IsRetroactive);
    }

    [Fact]
    public async Task GetAsOf_CoverageServiceUnavailable_ReturnsEmpty()
    {
        var handler = new StaticHandler(HttpStatusCode.InternalServerError, "");
        var svc = CreateService(handler);

        var result = await svc.GetAsOfAsync("t1", "MBR-1", new DateTime(2026, 1, 1));

        Assert.Empty(result.Coverages);
    }

    private class StaticHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;

        public StaticHandler(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var msg = new HttpResponseMessage(_status);
            if (!string.IsNullOrEmpty(_body))
                msg.Content = new StringContent(_body, Encoding.UTF8, "application/json");
            return Task.FromResult(msg);
        }
    }
}
