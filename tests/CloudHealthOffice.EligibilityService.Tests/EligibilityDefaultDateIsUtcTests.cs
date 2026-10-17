using System.Net;
using System.Text;
using EligibilityService.Adapters;
using EligibilityService.Controllers;
using EligibilityService.Models;
using EligibilityService.Repositories;
using EligibilityService.Services;
using CloudHealthOffice.Infrastructure.Edi.Interchange;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace CloudHealthOffice.EligibilityService.Tests;

/// <summary>
/// When the caller gives no service date, eligibility answers for today's
/// date in UTC from the injected <see cref="TimeProvider"/> — not the host's
/// local <c>DateTime.Today</c>, which is a different date for several hours
/// a day on any non-UTC host (and made the answer depend on where the pod ran).
/// The clock is 23:30 UTC on 2025-03-15: already 2025-03-16 east of UTC.
/// </summary>
public class EligibilityDefaultDateIsUtcTests
{
    private const string Tenant = "t1";
    private static readonly DateTimeOffset Now = new(2025, 3, 15, 23, 30, 0, TimeSpan.Zero);
    private static readonly DateTime Today = new(2025, 3, 15);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            });
        }
    }

    private static (EligibilityServiceImpl Service, RecordingHandler Handler) BuildService()
    {
        var handler = new RecordingHandler();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Services:CoverageService"] = "http://coverage-service/api/v1",
                ["Services:BenefitPlanService"] = "http://benefit-plan-service/api"
            })
            .Build();
        var service = new EligibilityServiceImpl(
            Substitute.For<IEligibilityRepository>(),
            new HttpClient(handler),
            NullLogger<EligibilityServiceImpl>.Instance,
            configuration,
            new EligibilityAdapterFactory(Array.Empty<IEligibilityAdapter>(), Substitute.For<IHttpClientFactory>(),
                configuration, NullLogger<EligibilityAdapterFactory>.Instance),
            new FixedClock(Now));
        return (service, handler);
    }

    private static string ActiveCoverageServiceDate(RecordingHandler handler)
    {
        var request = Assert.Single(handler.Requests, r => r.RequestUri!.AbsolutePath.EndsWith("/active"));
        return System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query)["serviceDate"]!;
    }

    [Fact]
    public async Task Accumulation_LooksUpCoverageForTodayInUtc()
    {
        var (svc, handler) = BuildService();

        await svc.GetAccumulationAsync(Tenant, "M1");

        Assert.Equal("2025-03-15", ActiveCoverageServiceDate(handler));
    }

    [Fact]
    public async Task AuthRequirement_LooksUpCoverageForTodayInUtc()
    {
        var (svc, handler) = BuildService();

        await svc.CheckAuthRequirementAsync(Tenant, "M1", "30", null);

        Assert.Equal("2025-03-15", ActiveCoverageServiceDate(handler));
    }

    private static (EligibilityController Controller, IEligibilityService Service) BuildController()
    {
        var service = Substitute.For<IEligibilityService>();
        service.QuickEligibilityCheckAsync(default!, default!, default, default)
            .ReturnsForAnyArgs((true, "1", "IND", "Active"));
        service.GetBenefitDetailsAsync(default!, default!, default, default)
            .ReturnsForAnyArgs(new List<EligibilityBenefit>());
        var controller = new EligibilityController(
            service,
            Substitute.For<IEligibilityRepository>(),
            Substitute.For<IEdi270Parser>(),
            Substitute.For<IEdi271Generator>(),
            NullLogger<EligibilityController>.Instance,
            timeProvider: new FixedClock(Now));
        var http = new DefaultHttpContext();
        http.Items["TenantId"] = Tenant;
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        return (controller, service);
    }

    [Fact]
    public async Task QuickCheck_WithoutServiceDate_ChecksTodayInUtc()
    {
        var (controller, service) = BuildController();

        var result = await controller.QuickCheck("M1");

        Assert.IsType<OkObjectResult>(result.Result);
        await service.Received(1).QuickEligibilityCheckAsync(Tenant, "M1", null, Today);
    }

    [Fact]
    public async Task Benefits_WithoutServiceDate_UseTodayInUtc()
    {
        var (controller, service) = BuildController();

        await controller.GetBenefits("M1");

        await service.Received(1).GetBenefitDetailsAsync(Tenant, "M1", null, Today);
    }

    [Fact]
    public async Task QuickCheck_WithServiceDate_UsesTheCallersDate()
    {
        var (controller, service) = BuildController();

        await controller.QuickCheck("M1", serviceDate: new DateTime(2024, 12, 31));

        await service.Received(1).QuickEligibilityCheckAsync(Tenant, "M1", null, new DateTime(2024, 12, 31));
    }
}
