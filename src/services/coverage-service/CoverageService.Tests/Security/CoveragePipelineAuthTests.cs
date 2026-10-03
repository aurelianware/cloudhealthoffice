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
        var client = Client("tenant-1", "dev-user", ChoRolePermissions.Finance);

        var response = await client.GetAsync(ByIdPath);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
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
