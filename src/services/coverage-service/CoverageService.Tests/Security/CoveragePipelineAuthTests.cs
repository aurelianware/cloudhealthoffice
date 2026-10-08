using System.Net;
using System.Net.Http.Json;
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
/// The real coverage-service pipeline. The tenant comes only from a validated
/// CHO token: an X-Tenant-ID header on its own no longer selects a tenant, and
/// coverage permissions are enforced.
/// </summary>
public class CoveragePipelineAuthTests : IClassFixture<CoveragePipelineAuthTests.Factory>
{
    public sealed class Factory : WebApplicationFactory<CoverageController>
    {
        public Mock<ICoverageRepository> Coverage { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            // Program.cs reads this while building, so it goes in as a host setting.
            builder.UseSetting("MongoDb:ConnectionString", "mongodb://fake-host:27017");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ICoverageRepository>();
                services.AddSingleton(Coverage.Object);
                services.RemoveAll<IPcpAssignmentRepository>();
                services.AddSingleton(Mock.Of<IPcpAssignmentRepository>());
            });
        }
    }

    private const string ByIdPath = "/api/v1/coverage/cov-1";
    private readonly Factory _factory;

    public CoveragePipelineAuthTests(Factory factory)
    {
        _factory = factory;
        _factory.Coverage.Reset();
        _factory.Coverage.Setup(r => r.GetByIdAsync(It.IsAny<string>(), "cov-1"))
            .ReturnsAsync((string tenant, string id) => new Coverage
            {
                Id = id, TenantId = tenant, MemberId = "M1", GroupNumber = "G", PlanId = "P",
                EffectiveDate = DateTime.UtcNow.Date.AddMonths(-1), Status = CoverageStatus.Active
            });
        _factory.Coverage.Setup(r => r.CreateAsync(It.IsAny<Coverage>())).ReturnsAsync((Coverage c) => c);
    }

    private HttpClient Client(string tenant, string subject, params string[] roles)
    {
        var client = _factory.CreateDefaultClient(new ChoDevelopmentTokenHandler(subject, roles));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", tenant);
        return client;
    }

    [Fact]
    public async Task NoToken_IsRejected()
    {
        var response = await _factory.CreateClient().GetAsync(ByIdPath);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task TenantHeaderWithoutToken_IsRejected()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-ID", "tenant-1");

        var response = await client.GetAsync(ByIdPath);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        _factory.Coverage.Verify(r => r.GetByIdAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task DevTenantHeaderWithoutToken_IsRejected()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Dev-Tenant-ID", "tenant-1");

        var response = await client.GetAsync(ByIdPath);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task TenantHeaderDisagreeingWithToken_IsForbidden()
    {
        var client = _factory.CreateDefaultClient();
        client.DefaultRequestHeaders.Authorization =
            new("Bearer", ChoDevelopmentAuth.UserToken("tenant-1", ChoRolePermissions.EnrollmentSpecialist));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", "tenant-2");

        var response = await client.GetAsync(ByIdPath);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _factory.Coverage.Verify(r => r.GetByIdAsync("tenant-2", It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ValidToken_IsServedFromTokenTenant()
    {
        var client = Client("tenant-1", "dev-user", ChoRolePermissions.MemberServices);

        var response = await client.GetAsync(ByIdPath);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.Coverage.Verify(r => r.GetByIdAsync("tenant-1", "cov-1"), Times.Once);
    }

    [Fact]
    public async Task RoleWithoutCoverageRead_IsForbidden()
    {
        // ProviderRelations holds neither coverage:read nor billing:read.
        var client = Client("tenant-1", "dev-user", ChoRolePermissions.ProviderRelations);

        var byId = await client.GetAsync(ByIdPath);
        var search = await client.GetAsync(BillingSearchPath);

        byId.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        search.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _factory.Coverage.VerifyNoOtherCalls();
    }

    /// <summary>
    /// Pins the wire shape eligibility-service reads from /active: a JSON array
    /// of Coverage, camelCase, enums by name ("status":"Terminated"). See
    /// EligibilityServiceCoverageSelectionTests, which uses this exact shape.
    /// </summary>
    [Fact]
    public async Task ActiveCoverage_IsAJsonArrayOfCoverage_WithEnumsByName()
    {
        _factory.Coverage.Setup(r => r.GetActiveCoverageByMemberIdAsync("tenant-1", "M1", new DateTime(2025, 3, 15), null))
            .ReturnsAsync(new List<Coverage>
            {
                new()
                {
                    Id = "cov-hlt", TenantId = "tenant-1", MemberId = "M1", GroupNumber = "GRP-100", PlanId = "PLAN-PPO",
                    CoverageLevel = "FAM", InsuranceLineCode = "HLT", EffectiveDate = new DateTime(2025, 1, 1),
                    TerminationDate = new DateTime(2025, 6, 30), Status = CoverageStatus.Terminated,
                    LineOfBusiness = LineOfBusiness.Commercial
                }
            });
        var client = Client("tenant-1", "dev-user", ChoRolePermissions.MemberServices);

        var response = await client.GetAsync("/api/v1/coverage/member/M1/active?serviceDate=2025-03-15");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.ValueKind.Should().Be(System.Text.Json.JsonValueKind.Array);
        var c = doc.RootElement[0];
        c.GetProperty("id").GetString().Should().Be("cov-hlt");
        c.GetProperty("planId").GetString().Should().Be("PLAN-PPO");
        c.GetProperty("groupNumber").GetString().Should().Be("GRP-100");
        c.GetProperty("insuranceLineCode").GetString().Should().Be("HLT");
        c.GetProperty("status").GetString().Should().Be("Terminated");
        c.GetProperty("lineOfBusiness").GetString().Should().Be("Commercial");
        c.GetProperty("effectiveDate").GetString().Should().Be("2025-01-01T00:00:00");
        c.GetProperty("terminationDate").GetString().Should().Be("2025-06-30T00:00:00");
    }

    // ── premium billing reads coverage with the Finance user's token ────

    /// <summary>The search premium-billing's CoverageServiceClient makes.</summary>
    private const string BillingSearchPath = "/api/v1/coverage?groupNumber=GRP-1&activeOnly=true&pageSize=100";

    private void SetupSearch()
        => _factory.Coverage.Setup(r => r.SearchAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<int>(), It.IsAny<string?>()))
            .ReturnsAsync(((IEnumerable<Coverage>)new List<Coverage>(), (string?)null));

    [Fact]
    public async Task Finance_CanSearchCoverageForBilling()
    {
        // Before: Finance (billing:read, no coverage:read) got 403 here, so a
        // billing run started by a Finance user priced no sponsor.
        SetupSearch();
        var client = Client("tenant-1", "finance-user", ChoRolePermissions.Finance);

        var response = await client.GetAsync(BillingSearchPath);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.Coverage.Verify(r => r.SearchAsync("tenant-1", null, "GRP-1", null, true, 100, null), Times.Once);
    }

    [Fact]
    public async Task BillingRead_AloneReachesOnlyTheSearch()
    {
        // A custom role holding billing:read only: the search, nothing else.
        SetupSearch();
        var token = ChoDevelopmentAuth.UserTokenIssuer()
            .IssueUserToken("billing-clerk", "tenant-1", new[] { "BillingClerk" }, new[] { "billing:read" });
        var client = _factory.CreateDefaultClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);

        (await client.GetAsync(BillingSearchPath)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync(ByIdPath)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.GetAsync("/api/v1/coverage/member/M1/active")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.GetAsync("/api/v1/coverage/group/GRP-1/summary")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Finance_CannotWriteCoverage()
    {
        var client = Client("tenant-1", "finance-user", ChoRolePermissions.Finance);

        var create = await client.PostAsJsonAsync("/api/v1/coverage", new
        {
            memberId = "M1", groupNumber = "G", planId = "P", effectiveDate = DateTime.UtcNow.Date
        });
        var update = await client.PutAsJsonAsync(ByIdPath, new { planId = "P2" });
        var delete = await client.DeleteAsync(ByIdPath);
        var terminate = await client.PostAsJsonAsync("/api/v1/coverage/member/M1/terminate",
            new { terminationDate = DateTime.UtcNow.Date, reason = "x" });
        var pcp = await client.PutAsJsonAsync("/api/v1/coverage/member/M1/pcp", new { npi = "1234567893" });
        var reinstate = await client.PostAsync("/api/v1/coverage/cov-1/reinstate", null);

        foreach (var response in new[] { create, update, delete, terminate, pcp, reinstate })
            response.StatusCode.Should().Be(HttpStatusCode.Forbidden, response.RequestMessage!.RequestUri!.ToString());
        _factory.Coverage.Verify(r => r.CreateAsync(It.IsAny<Coverage>()), Times.Never);
        _factory.Coverage.Verify(r => r.UpdateAsync(It.IsAny<Coverage>()), Times.Never);
    }

    [Fact]
    public async Task ReadOnlyRole_CannotWrite()
    {
        // MemberServices holds coverage:read but not coverage:write.
        var client = Client("tenant-1", "dev-user", ChoRolePermissions.MemberServices);

        var response = await client.DeleteAsync(ByIdPath);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _factory.Coverage.Verify(r => r.UpdateAsync(It.IsAny<Coverage>()), Times.Never);
    }

    [Fact]
    public async Task Create_RecordsTokenSubjectAndTokenTenant()
    {
        var client = Client("tenant-1", "enroll-user-9", ChoRolePermissions.EnrollmentSpecialist);

        var response = await client.PostAsJsonAsync("/api/v1/coverage", new
        {
            memberId = "M1", groupNumber = "G", planId = "P", effectiveDate = DateTime.UtcNow.Date,
            tenantId = "tenant-2", createdBy = "someone-else"
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        _factory.Coverage.Verify(r => r.CreateAsync(It.Is<Coverage>(c =>
            c.CreatedBy == "enroll-user-9" && c.TenantId == "tenant-1")), Times.Once);
    }
}
