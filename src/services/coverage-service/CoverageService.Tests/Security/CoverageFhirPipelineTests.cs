using System.Net;
using System.Text.Json.Nodes;
using CloudHealthOffice.Infrastructure.Security;
using CoverageService.Controllers;
using CoverageService.Models;
using CoverageService.Repositories;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CoverageService.Tests.Security;

/// <summary>
/// The FHIR Coverage projection endpoints through the real pipeline (ADR 018):
/// they need coverage:read (billing:read is not enough), run in the token's
/// tenant, translate FHIR statuses to CHO statuses, reject unknown input, and
/// return projected resources. Query semantics are covered against real MongoDB
/// in CoverageFhirSearchRepositoryTests.
/// </summary>
public class CoverageFhirPipelineTests : IClassFixture<CoverageFhirPipelineTests.Factory>
{
    public sealed class Factory : WebApplicationFactory<CoverageController>
    {
        public Mock<ICoverageRepository> Coverage { get; } = new();
        public Mock<ICoverageFhirSearchRepository> Search { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("MongoDb:ConnectionString", "mongodb://fake-host:27017");
            builder.UseSetting("Fhir:Coverage:PayorDisplay", "Example Health Plan");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ICoverageRepository>();
                services.AddSingleton(Coverage.Object);
                services.RemoveAll<ICoverageFhirSearchRepository>();
                services.AddSingleton(Search.Object);
                services.RemoveAll<IPcpAssignmentRepository>();
                services.AddSingleton(Mock.Of<IPcpAssignmentRepository>());
            });
        }
    }

    private const string Tenant = "tenant-cov-fhir";
    private readonly Factory _factory;
    private readonly List<(string Tenant, CoverageFhirSearchQuery Query)> _calls = new();

    public CoverageFhirPipelineTests(Factory factory)
    {
        _factory = factory;
        _factory.Coverage.Reset();
        _factory.Search.Reset();
        _factory.Coverage.Setup(r => r.GetByIdAsync(It.IsAny<string>(), "cov-1"))
            .ReturnsAsync((string tenant, string id) => Sample(id, tenant));
        _factory.Search
            .Setup(r => r.SearchAsync(It.IsAny<string>(), It.IsAny<CoverageFhirSearchQuery>(), It.IsAny<CancellationToken>()))
            .Callback((string tenant, CoverageFhirSearchQuery q, CancellationToken _) => _calls.Add((tenant, q)))
            .ReturnsAsync((string tenant, CoverageFhirSearchQuery _, CancellationToken _) =>
                ((IReadOnlyList<Coverage>)[Sample("cov-1", tenant), Sample("cov-2", tenant)], 12L));
    }

    private static Coverage Sample(string id, string tenant) => new()
    {
        Id = id,
        TenantId = tenant,
        MemberId = "M1",
        GroupNumber = "G",
        PlanId = "P",
        EffectiveDate = new DateTime(2025, 1, 1),
        Status = CoverageStatus.Active,
    };

    private HttpClient As(params string[] roles)
    {
        var client = _factory.CreateDefaultClient(new ChoDevelopmentTokenHandler("user-1", roles));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", Tenant);
        return client;
    }

    private static async Task<JsonObject> Json(HttpResponseMessage r) => JsonNode.Parse(await r.Content.ReadAsStringAsync())!.AsObject();

    [Fact]
    public async Task Read_returns_the_fhir_projection_with_the_configured_payor()
    {
        var response = await As(ChoRolePermissions.MemberServices).GetAsync("/api/v1/coverage/cov-1/fhir");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/fhir+json");
        var json = await Json(response);
        json["resourceType"]!.ToString().Should().Be("Coverage");
        json["beneficiary"]!["reference"]!.ToString().Should().Be("Patient/M1");
        json["payor"]![0]!["display"]!.ToString().Should().Be("Example Health Plan");
    }

    [Fact]
    public async Task Read_of_an_unknown_coverage_is_not_found()
    {
        (await As(ChoRolePermissions.MemberServices).GetAsync("/api/v1/coverage/nope/fhir"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Search_runs_in_the_tokens_tenant_and_maps_fhir_statuses_to_cho_statuses()
    {
        var response = await As(ChoRolePermissions.MemberServices).GetAsync(
            "/api/v1/coverage/fhir/Coverage?memberId=M1&status=active,cancelled&lineOfBusiness=Medicaid"
            + "&lastUpdatedFrom=2026-01-01T00:00:00Z&offset=10&limit=2");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await Json(response);
        body["total"]!.GetValue<long>().Should().Be(12);
        body["items"]!.AsArray().Select(i => i!["resource"]!["id"]!.ToString()).Should().Equal("cov-1", "cov-2");
        body["items"]![0]!["memberId"]!.ToString().Should().Be("M1");

        var (tenant, query) = _calls.Should().ContainSingle().Subject;
        tenant.Should().Be(Tenant);
        query.MemberId.Should().Be("M1");
        query.Statuses.Should().BeEquivalentTo(new[]
        {
            CoverageStatus.Active, CoverageStatus.COBRA, CoverageStatus.Suspended, CoverageStatus.Terminated,
        });
        query.LinesOfBusiness.Should().Equal(LineOfBusiness.Medicaid);
        query.Offset.Should().Be(10);
        query.Limit.Should().Be(2);
    }

    [Fact]
    public async Task Entered_in_error_alone_matches_nothing_without_querying()
    {
        var response = await As(ChoRolePermissions.MemberServices).GetAsync("/api/v1/coverage/fhir/Coverage?status=entered-in-error");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Json(response))["total"]!.GetValue<long>().Should().Be(0);
        _calls.Should().BeEmpty();
    }

    [Theory]
    [InlineData("status=lapsed")]
    [InlineData("lineOfBusiness=Dental")]
    [InlineData("limit=101")]
    [InlineData("offset=-1")]
    public async Task Unknown_or_out_of_range_input_is_rejected(string query)
    {
        (await As(ChoRolePermissions.MemberServices).GetAsync("/api/v1/coverage/fhir/Coverage?" + query))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _calls.Should().BeEmpty();
    }

    [Theory]
    [InlineData("/api/v1/coverage/fhir/Coverage")]
    [InlineData("/api/v1/coverage/cov-1/fhir")]
    public async Task Projections_need_coverage_read_and_billing_read_is_not_enough(string path)
    {
        (await As(ChoRolePermissions.Finance).GetAsync(path)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await As(ChoRolePermissions.ClaimsExaminer).GetAsync(path)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await _factory.CreateClient().GetAsync(path)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        _calls.Should().BeEmpty();
    }
}
