using System.Net;
using System.Text;
using EligibilityService.Adapters;
using EligibilityService.Models;
using EligibilityService.Repositories;
using EligibilityService.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace CloudHealthOffice.EligibilityService.Tests;

/// <summary>
/// coverage-service's GET /coverage/member/{id}/active answers a JSON array of
/// its Coverage documents, camelCase with enums by name (pinned on the
/// coverage-service side by CoveragePipelineAuthTests
/// .ActiveCoverage_IsAJsonArrayOfCoverage_WithEnumsByName). EligibilityServiceImpl
/// used to read it as a single object, which threw at runtime.
/// </summary>
public class EligibilityServiceCoverageSelectionTests
{
    private const string Tenant = "t1";

    /// <summary>One coverage exactly as coverage-service serializes it.</summary>
    private static string Wire(
        string id, string planId, string? line, string status, string effective, string? termination = null,
        string group = "GRP-100", string level = "FAM") =>
        $$"""
        {"tenantId":"t1","id":"{{id}}","memberId":"M1","groupNumber":"{{group}}","planId":"{{planId}}","coverageLevel":"{{level}}","insuranceLineCode":{{Str(line)}},"effectiveDate":"{{effective}}T00:00:00","terminationDate":{{Str(termination is null ? null : termination + "T00:00:00")}},"status":"{{status}}","lineOfBusiness":"Commercial","isCOBRA":false,"cobraEffectiveDate":null,"medicareCoverage":null,"otherInsurance":null,"monthlyPremium":null,"employerContribution":null,"maintenanceTypeCode":"021","maintenanceReasonCode":null,"pcpNpi":null,"pcpName":null,"pcpAssignmentDate":null,"pcpAssignmentMethod":null,"previousPcpNpi":null,"createdDate":"2025-01-02T10:00:00.1234567Z","lastUpdatedDate":"2025-01-02T10:00:00.1234567Z","createdBy":"enrollment-import","lastUpdatedBy":null}
        """;

    private static string Str(string? v) => v is null ? "null" : $"\"{v}\"";

    private static string Array(params string[] items) => "[" + string.Join(",", items) + "]";

    private sealed class RoutingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static (EligibilityServiceImpl Service, RoutingHandler Handler) Build(string activeBody, HttpStatusCode activeStatus = HttpStatusCode.OK)
    {
        var handler = new RoutingHandler(r =>
            r.RequestUri!.AbsolutePath.EndsWith("/active") ? Json(activeStatus, activeBody)
            : r.RequestUri.AbsolutePath.EndsWith("/benefits") ? Json(HttpStatusCode.OK, "[]")
            : Json(HttpStatusCode.NotFound, "{}"));
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Services:CoverageService"] = "http://coverage-service/api/v1",
                ["Services:BenefitPlanService"] = "http://benefit-plan-service/api"
            })
            .Build();
        var factory = Substitute.For<IHttpClientFactory>();
        var service = new EligibilityServiceImpl(
            Substitute.For<IEligibilityRepository>(),
            new HttpClient(handler),
            NullLogger<EligibilityServiceImpl>.Instance,
            configuration,
            new EligibilityAdapterFactory(System.Array.Empty<IEligibilityAdapter>(), factory, configuration,
                NullLogger<EligibilityAdapterFactory>.Instance));
        return (service, handler);
    }

    [Fact]
    public async Task QuickCheck_ReadsCoverageServiceListResponse()
    {
        var (svc, handler) = Build(Array(Wire("cov-1", "PLAN-PPO", "HLT", "Active", "2025-01-01")));

        var result = await svc.QuickEligibilityCheckAsync(Tenant, "M1", null, new DateTime(2025, 3, 15));

        Assert.True(result.IsActive);
        Assert.Equal("1", result.StatusCode);
        Assert.Equal("FAM", result.CoverageLevel);
        Assert.Contains("serviceDate=2025-03-15", handler.Requests[0].RequestUri!.Query);
    }

    [Fact]
    public async Task QuickCheck_TerminatedCoverage_CoversServiceDateWithinItsSpan()
    {
        var (svc, _) = Build(Array(Wire("cov-1", "PLAN-PPO", "HLT", "Terminated", "2025-01-01", "2025-06-30")));

        var within = await svc.QuickEligibilityCheckAsync(Tenant, "M1", null, new DateTime(2025, 6, 30));
        var after = await svc.QuickEligibilityCheckAsync(Tenant, "M1", null, new DateTime(2025, 7, 1));

        Assert.True(within.IsActive);
        Assert.False(after.IsActive);
        Assert.Equal("6", after.StatusCode);
    }

    [Fact]
    public async Task QuickCheck_NoCoverage404_IsNotActive()
    {
        var (svc, _) = Build("""{"memberId":"M1","message":"No active coverage found for member on service date"}""",
            HttpStatusCode.NotFound);

        var result = await svc.QuickEligibilityCheckAsync(Tenant, "M1", null, new DateTime(2025, 3, 15));

        Assert.False(result.IsActive);
        Assert.Equal("No coverage found", result.Message);
    }

    [Fact]
    public async Task QuickCheck_DentalOnlyMember_IsNotEligibleForAMedicalCheck()
    {
        var (svc, _) = Build(Array(Wire("cov-den", "PLAN-DEN", "DEN", "Active", "2025-01-01")));

        var result = await svc.QuickEligibilityCheckAsync(Tenant, "M1", null, new DateTime(2025, 3, 15));

        Assert.False(result.IsActive);
    }

    [Theory]
    [InlineData("30", "PLAN-PPO")]
    [InlineData(null, "PLAN-PPO")]
    [InlineData("35", "PLAN-DEN")]
    [InlineData("AL", "PLAN-VIS")]
    [InlineData("AN", "PLAN-VIS")]
    public async Task BenefitDetails_UsesThePlanOfTheServiceTypesInsuranceLine(string? serviceType, string expectedPlan)
    {
        // Dental listed first so "first entry" would be wrong for a medical question.
        var (svc, handler) = Build(Array(
            Wire("cov-den", "PLAN-DEN", "DEN", "Active", "2025-01-01"),
            Wire("cov-hlt", "PLAN-PPO", "HLT", "Active", "2025-01-01"),
            Wire("cov-vis", "PLAN-VIS", "VIS", "Active", "2025-01-01")));

        await svc.GetBenefitDetailsAsync(Tenant, "M1", serviceType, new DateTime(2025, 3, 15));

        Assert.Contains(handler.Requests, r => r.RequestUri!.AbsolutePath == $"/api/benefit-plans/{expectedPlan}/benefits");
    }

    [Fact]
    public async Task BenefitDetails_CoverageWithNoInsuranceLine_IsTreatedAsHealth()
    {
        var (svc, handler) = Build(Array(Wire("cov-legacy", "PLAN-OLD", null, "Active", "2024-01-01")));

        await svc.GetBenefitDetailsAsync(Tenant, "M1", "30", new DateTime(2025, 3, 15));

        Assert.Contains(handler.Requests, r => r.RequestUri!.AbsolutePath == "/api/benefit-plans/PLAN-OLD/benefits");
    }

    [Fact]
    public void Select_PrefersRequestedGroup_ThenLatestEffectiveDate()
    {
        var coverages = new List<CoverageDto>
        {
            new() { Id = "old", GroupNumber = "G1", InsuranceLineCode = "HLT", Status = 1, EffectiveDate = new DateTime(2024, 1, 1) },
            new() { Id = "new", GroupNumber = "G1", InsuranceLineCode = "HLT", Status = 1, EffectiveDate = new DateTime(2025, 1, 1) },
            new() { Id = "other-group", GroupNumber = "G2", InsuranceLineCode = "HLT", Status = 1, EffectiveDate = new DateTime(2025, 2, 1) }
        };

        Assert.Equal("other-group", EligibilityServiceImpl.SelectCoverage(coverages, new DateTime(2025, 3, 1), null, null)!.Id);
        Assert.Equal("new", EligibilityServiceImpl.SelectCoverage(coverages, new DateTime(2025, 3, 1), null, "G1")!.Id);
    }

    [Theory]
    [InlineData(4)] // Suspended
    [InlineData(0)] // unknown
    public void Select_NotInForceStatus_IsSkipped(int status)
    {
        var coverages = new List<CoverageDto>
        {
            new() { Id = "c", InsuranceLineCode = "HLT", Status = status, EffectiveDate = new DateTime(2025, 1, 1) }
        };

        Assert.Null(EligibilityServiceImpl.SelectCoverage(coverages, new DateTime(2025, 3, 1), null, null));
    }

    [Fact]
    public async Task QuickCheck_FutureDatedAddStillPending_IsEligibleOnItsEffectiveDate_NotTheDayBefore()
    {
        // coverage-service's /active returns it as stored: Pending (the sweep
        // hasn't run). Pending is only "not yet effective", so the span decides.
        var (svc, _) = Build(Array(Wire("cov-1", "PLAN-PPO", "HLT", "Pending", "2026-01-01")));

        var dayBefore = await svc.QuickEligibilityCheckAsync(Tenant, "M1", null, new DateTime(2025, 12, 31));
        var effectiveDay = await svc.QuickEligibilityCheckAsync(Tenant, "M1", null, new DateTime(2026, 1, 1));

        Assert.False(dayBefore.IsActive);
        Assert.True(effectiveDay.IsActive);
    }

    [Fact]
    public async Task ChoAdapter_FutureDatedAddStillPending_IsEligibleOnItsEffectiveDate_NotTheDayBefore()
    {
        var body = Array(Wire("cov-1", "PLAN-PPO", "HLT", "Pending", "2026-01-01"));
        var handler = new RoutingHandler(r =>
            r.RequestUri!.AbsolutePath.EndsWith("/active") ? Json(HttpStatusCode.OK, body)
            : r.RequestUri.AbsolutePath.EndsWith("/benefits") ? Json(HttpStatusCode.OK, "[]")
            : Json(HttpStatusCode.NotFound, ""));
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("EligibilityDefault").Returns(new HttpClient(handler));
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Services:CoverageService"] = "http://c/api/v1" })
            .Build();
        var adapter = new ChoEligibilityAdapter(factory, configuration, Substitute.For<ILogger<ChoEligibilityAdapter>>());

        var dayBefore = await adapter.VerifyEligibilityAsync(new EligibilityAdapterRequest
        {
            TenantId = Tenant, SubscriberId = "M1", ServiceDate = new DateTime(2025, 12, 31), ServiceTypeCode = "30"
        });
        var effectiveDay = await adapter.VerifyEligibilityAsync(new EligibilityAdapterRequest
        {
            TenantId = Tenant, SubscriberId = "M1", ServiceDate = new DateTime(2026, 1, 1), ServiceTypeCode = "30"
        });

        Assert.False(dayBefore.IsEligible);
        Assert.True(effectiveDay.IsEligible);
    }

    [Fact]
    public async Task ChoAdapter_ReadsEnumsByName()
    {
        // The 270/271 adapter's DTO held Status as int; "status":"Active" threw.
        var body = Array(Wire("cov-1", "PLAN-PPO", "HLT", "Active", "2025-01-01"));
        var handler = new RoutingHandler(r =>
            r.RequestUri!.AbsolutePath.EndsWith("/active") ? Json(HttpStatusCode.OK, body)
            : r.RequestUri.AbsolutePath.EndsWith("/benefits") ? Json(HttpStatusCode.OK, "[]")
            : Json(HttpStatusCode.NotFound, ""));
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("EligibilityDefault").Returns(new HttpClient(handler));
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Services:CoverageService"] = "http://c/api/v1" })
            .Build();
        var adapter = new ChoEligibilityAdapter(factory, configuration, Substitute.For<ILogger<ChoEligibilityAdapter>>());

        var result = await adapter.VerifyEligibilityAsync(new EligibilityAdapterRequest
        {
            TenantId = Tenant, SubscriberId = "M1", ServiceDate = new DateTime(2025, 3, 15), ServiceTypeCode = "30"
        });

        Assert.True(result.IsEligible);
        Assert.Equal("PLAN-PPO", result.PlanId);
        Assert.Equal(LineOfBusiness.Commercial, result.LineOfBusiness);
    }

    [Fact]
    public async Task Temporal_ReadsEnumsByName()
    {
        var body = Array(Wire("cov-1", "PLAN-PPO", "HLT", "Active", "2025-01-01"));
        var handler = new RoutingHandler(_ => Json(HttpStatusCode.OK, body));
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("EligibilityDefault").Returns(new HttpClient(handler));
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Services:CoverageService"] = "http://c/api/v1" })
            .Build();
        var svc = new TemporalEligibilityService(factory, new StubAccumulatorClient(), configuration,
            Substitute.For<ILogger<TemporalEligibilityService>>());

        var result = await svc.GetAsOfAsync(Tenant, "M1", new DateTime(2025, 3, 15));

        Assert.Single(result.Coverages);
        Assert.Equal("PLAN-PPO", result.Coverages[0].PlanId);
    }
}
